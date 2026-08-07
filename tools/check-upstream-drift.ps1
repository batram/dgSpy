<#
.SYNOPSIS
    Fail if this fork diverges from its dnSpyEx baseline in ways nobody wrote down.

.DESCRIPTION
    dgSpy is a fork of dnSpyEx. Commit 399ed7297 set the tree to an older dgSpy
    tree and so reverted 57 shared files to a pre-dnSpyEx state. Nothing caught
    it: the code compiled, and the test gate only exercises dgSpy's own MCP
    surface, never dnSpyEx's UI. The bug surfaced weeks later as a missing entry
    in a dropdown.

    This check makes that class of damage loud. Every file differing from the
    baseline commit must be listed, with a reason, in the allowlist. Unlisted
    drift fails. A stale entry -- one matching nothing any more -- also fails,
    so the file cannot quietly rot into a licence to overwrite something.

    The comparison is against a pinned commit, not a moving branch tip, so a new
    dnSpyEx release cannot turn a green run red on its own.

    ASCII-only by policy: an em dash in a .ps1 parses as a stray quote under CP1252.

.EXAMPLE
    powershell -NoProfile -File tools\check-upstream-drift.ps1

.EXAMPLE
    # What is drifting, grouped, without failing the run
    powershell -NoProfile -File tools\check-upstream-drift.ps1 -Report

.EXAMPLE
    # Check a specific commit rather than the working tree
    powershell -NoProfile -File tools\check-upstream-drift.ps1 -Revision HEAD
#>
[CmdletBinding()]
param(
    # Compare this revision instead of the working tree.
    [string]$Revision,

    # Print every drifting file with the reason that covers it, and do not fail.
    [switch]$Report,

    # Treat stale allowlist entries as informational rather than failures.
    [switch]$AllowStale
)

$ErrorActionPreference = 'Stop'

$repoRoot     = Split-Path -Parent $PSScriptRoot
$baselineFile = Join-Path $PSScriptRoot 'upstream-baseline.txt'
$allowFile    = Join-Path $PSScriptRoot 'upstream-drift-allowlist.txt'

foreach ($f in @($baselineFile, $allowFile)) {
    if (-not (Test-Path $f)) { throw "missing required file: $f" }
}

# --- baseline -------------------------------------------------------------

$baseline = (Get-Content $baselineFile |
    Where-Object { $_ -notmatch '^\s*#' -and $_.Trim() } |
    Select-Object -First 1).Trim()

if ($baseline -notmatch '^[0-9a-f]{40}$') {
    throw "tools/upstream-baseline.txt does not contain a full 40-character commit sha"
}

Push-Location $repoRoot
try {
    & git cat-file -e "$baseline^{commit}" 2>$null
    if ($LASTEXITCODE -ne 0) {
        Write-Output "FAIL  baseline commit $baseline is not in this clone."
        Write-Output "      Fetch it first:  git fetch https://github.com/dnSpyEx/dnSpy master"
        exit 1
    }

    # --- allowlist --------------------------------------------------------

    $rules = @()
    $lineNo = 0
    foreach ($line in Get-Content $allowFile) {
        $lineNo++
        $t = $line.Trim()
        if (-not $t -or $t.StartsWith('#')) { continue }
        # split on the first run of whitespace: pattern, then free-text reason
        if ($t -match '^(\S+)\s+(.*\S)\s*$') {
            $rules += [pscustomobject]@{
                Pattern = $matches[1]
                Reason  = $matches[2]
                Line    = $lineNo
                Hits    = 0
            }
        } else {
            Write-Output "FAIL  tools/upstream-drift-allowlist.txt line ${lineNo}: entry has no reason -- '$t'"
            exit 1
        }
    }
    if ($rules.Count -eq 0) { throw "allowlist contains no rules" }

    function Test-Match([string]$path, [string]$pattern) {
        if ($pattern.EndsWith('/')) { return $path.StartsWith($pattern, 'Ordinal') }
        if ($pattern.Contains('*'))  { return $path -like $pattern }
        return $path -eq $pattern
    }

    # --- what differs -----------------------------------------------------

    if ($Revision) { $raw = & git diff --name-status $baseline $Revision }
    else           { $raw = & git diff --name-status $baseline }
    if ($LASTEXITCODE -ne 0) { throw "git diff against $baseline failed" }

    $changes = @()
    foreach ($line in $raw) {
        if ($line -match '^([A-Z])\d*\s+(.+)$') {
            $changes += [pscustomobject]@{ Status = $matches[1]; Path = $matches[2].Trim() }
        }
    }

    # git diff cannot see files that were never added, so a brand new file
    # dropped into an upstream tree would otherwise sail past this check until
    # someone staged it. Fold untracked files in as additions.
    if (-not $Revision) {
        $untracked = & git ls-files --others --exclude-standard
        if ($LASTEXITCODE -ne 0) { throw "git ls-files --others failed" }
        foreach ($u in $untracked) {
            $p = $u.Trim()
            if ($p) { $changes += [pscustomobject]@{ Status = 'A'; Path = $p } }
        }
    }

    $violations = @()
    $covered    = @()
    foreach ($c in $changes) {
        $rule = $rules | Where-Object { Test-Match $c.Path $_.Pattern } | Select-Object -First 1
        if ($rule) {
            $rule.Hits++
            $covered += [pscustomobject]@{ Status = $c.Status; Path = $c.Path; Reason = $rule.Reason }
        } else {
            $violations += $c
        }
    }

    $stale = $rules | Where-Object { $_.Hits -eq 0 }

    # --- output -----------------------------------------------------------

    $what = if ($Revision) { $Revision } else { 'working tree' }
    Write-Output "baseline : $baseline (dnSpyEx)"
    Write-Output "compared : $what"
    Write-Output "drifting : $($changes.Count) files, $($covered.Count) accounted for"
    Write-Output ""

    if ($Report) {
        foreach ($g in ($covered | Group-Object Reason | Sort-Object Name)) {
            Write-Output ("  " + $g.Name + "  [" + $g.Count + "]")
            foreach ($c in ($g.Group | Sort-Object Path)) {
                Write-Output ("      " + $c.Status + "  " + $c.Path)
            }
        }
        Write-Output ""
    }

    $failed = $false

    if ($violations.Count -gt 0) {
        $failed = $true
        Write-Output "FAIL  $($violations.Count) file(s) differ from dnSpyEx with no recorded reason:"
        Write-Output ""
        foreach ($v in ($violations | Sort-Object Path)) {
            Write-Output ("      " + $v.Status + "  " + $v.Path)
        }
        Write-Output ""
        Write-Output "      A (added) / M (modified) / D (deleted), relative to dnSpyEx."
        Write-Output "      If the change is deliberate, add it to tools/upstream-drift-allowlist.txt"
        Write-Output "      with a reason. If it is not, restore the file instead of listing it:"
        Write-Output "          git checkout $($baseline.Substring(0,9)) -- <path>"
        Write-Output ""
    }

    if ($stale.Count -gt 0) {
        if (-not $AllowStale) { $failed = $true }
        $label = if ($AllowStale) { 'note' } else { 'FAIL' }
        Write-Output "$label  $($stale.Count) allowlist entr(y/ies) match nothing and should be deleted:"
        Write-Output ""
        foreach ($s in $stale) {
            Write-Output ("      line " + $s.Line + ": " + $s.Pattern)
        }
        Write-Output ""
        Write-Output "      A pattern matching nothing is usually good news -- the drift is gone."
        Write-Output "      Delete the line so it cannot later excuse a change nobody reviewed."
        Write-Output ""
    }

    if ($failed) { exit 1 }

    Write-Output "OK    all drift from dnSpyEx is accounted for."
    exit 0
}
finally {
    Pop-Location
}
