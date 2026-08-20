#requires -Version 5.1
<#
Runs one cross-identity smoke the cheapest way that works here, and says which way it chose.
ASCII-only. Returns the smoke's exit code; throws only when it could not run at all.

Three paths, in order:

  1. already elevated  -> run the smoke directly, in-process;
  2. task registered   -> trigger it and wait, which needs no elevation and no prompt;
  3. neither           -> throw, naming the one-time setup command.

The point of 2 is that a gate should not require an interactive elevation prompt on every run. See
Register-CrossIdentitySmokeTasks.ps1 for why a scheduled task rather than a standing privilege grant.
#>
[CmdletBinding()]
param(
	[Parameter(Mandatory = $true)][ValidateSet('net48', 'coreclr')][string]$Which,
	[Parameter(Mandatory = $true)][string]$RepoRoot,
	[Parameter(Mandatory = $true)][string]$RunDirectory,
	[int]$TimeoutSeconds = 900
)
$ErrorActionPreference = 'Stop'
$script = if ($Which -eq 'net48') { 'run-hooklab-cross-identity-smoke.ps1' } else { 'run-hooklab-cross-identity-coreclr-smoke.ps1' }
$elevated = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if ($elevated) {
	Write-Host "cross-identity ($Which): running directly, this shell is elevated" -ForegroundColor DarkGray
	& (Join-Path $RepoRoot ('tests\' + $script)) -RunDirectory $RunDirectory
	return $LASTEXITCODE
}

$taskName = "cross-identity-$Which"
$task = Get-ScheduledTask -TaskPath '\dgSpy\' -TaskName $taskName -ErrorAction SilentlyContinue
if (-not $task) {
	throw "This leg needs SeDebugPrivilege to attach across accounts, and this shell is not elevated. Register the tasks once, elevated: powershell -NoProfile -ExecutionPolicy Bypass -File tests\TestSupport\Register-CrossIdentitySmokeTasks.ps1 - after which the gate runs it without elevation or prompts."
}

$exchange = Join-Path $env:ProgramData 'dgSpy\fixture'
New-Item -ItemType Directory -Force -Path $exchange | Out-Null
New-Item -ItemType Directory -Force -Path $RunDirectory | Out-Null
# The result lands in the run directory rather than the shared exchange, because the task runs
# ELEVATED and this does not: a result file written by an administrator token cannot be replaced by
# this one, so the second run failed with "Access to the path is denied" before it started anything.
# The run directory is created here, per run, by this token. The request stays in the shared location
# because the direction of that transfer is the safe one - written unelevated, read elevated.
$resultPath = Join-Path $RunDirectory 'task-result.json'
if (Test-Path $resultPath) { Remove-Item $resultPath -Force }
# DGSPY_HIDDEN_DESKTOP_LAUNCHER is optional and machine-local: a script that starts a process on a
# private desktop, so a task running in the interactive session does not put dnSpy on the user's screen.
# Passed through rather than read by the task, which inherits none of this shell's environment.
$request = [ordered]@{ repo_root = $RepoRoot; layout_root = $env:DGSPY_LAYOUT_ROOT; run_directory = $RunDirectory; hidden_desktop_launcher = $env:DGSPY_HIDDEN_DESKTOP_LAUNCHER; requested_utc = [DateTime]::UtcNow.ToString('o') }
Set-Content -LiteralPath (Join-Path $exchange ("request-$Which.json")) -Value ($request | ConvertTo-Json) -Encoding utf8

# Say it out loud rather than only recording it. Unset, this leg runs on the interactive desktop and
# puts a real dnSpy window on whoever's screen is attached - and it still passes, so nothing else in
# the run reports it. That silence is how it went unnoticed on 2026-08-20: the outcome was visible in
# task-result.json as detail="completed" instead of "completed on the hidden desktop", which nobody
# reads on a green run. Not fatal, because a headless runner has no screen to protect and no launcher
# to name.
if ([string]::IsNullOrWhiteSpace($env:DGSPY_HIDDEN_DESKTOP_LAUNCHER)) {
	Write-Host ("cross-identity ($Which): DGSPY_HIDDEN_DESKTOP_LAUNCHER is not set, so this leg runs on the " +
		"interactive desktop and dnSpy will appear on screen. Set it to your hidden-desktop launcher to keep it quiet.") -ForegroundColor Yellow
}
elseif (-not (Test-Path $env:DGSPY_HIDDEN_DESKTOP_LAUNCHER)) {
	Write-Host ("cross-identity ($Which): DGSPY_HIDDEN_DESKTOP_LAUNCHER points at nothing (" +
		$env:DGSPY_HIDDEN_DESKTOP_LAUNCHER + "), so this leg runs on the interactive desktop.") -ForegroundColor Yellow
}

Write-Host "cross-identity ($Which): triggering scheduled task \dgSpy\$taskName" -ForegroundColor DarkGray
Start-ScheduledTask -TaskPath '\dgSpy\' -TaskName $taskName

# Wait on the TASK, not on the result file alone. A task that dies without writing one would otherwise
# hold the gate until its timeout, reporting nothing - the same trap as watching a log for a marker a
# dead process can never write.
$deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
$sawRunning = $false
while ([DateTime]::UtcNow -lt $deadline) {
	Start-Sleep -Seconds 2
	$state = (Get-ScheduledTask -TaskPath '\dgSpy\' -TaskName $taskName).State
	if ($state -eq 'Running') { $sawRunning = $true; continue }
	# Ready before it ever ran means the trigger has not taken effect yet; ready after means it is done.
	if ($sawRunning) { break }
	if (Test-Path $resultPath) { break }
}

if (-not (Test-Path $resultPath)) {
	throw "The scheduled task \dgSpy\$taskName finished or timed out without writing a result. Its own log is in $RunDirectory if it got that far; Get-ScheduledTaskInfo -TaskPath \dgSpy\ -TaskName $taskName reports its last result."
}
$result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
if ($result.detail -and $result.detail -ne 'completed') { Write-Host ("cross-identity (" + $Which + "): " + $result.detail) -ForegroundColor Yellow }
return [int]$result.exit_code
