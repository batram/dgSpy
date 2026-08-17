#requires -Version 5.1
[CmdletBinding()]
param([switch]$Elevated,[string]$LogPath)

$ErrorActionPreference = 'Stop'
$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new()
$repo = Split-Path $PSScriptRoot -Parent
if ([string]::IsNullOrWhiteSpace($LogPath)) { $LogPath = Join-Path $PSScriptRoot ('artifacts\road1-watcher-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.log') }
if (-not $Elevated) {
    $shell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $arguments = @('-NoProfile','-ExecutionPolicy','Bypass','-File',$PSCommandPath,'-Elevated','-LogPath',$LogPath)
    $process = Start-Process -FilePath $shell -Verb RunAs -ArgumentList $arguments -Wait -PassThru
    Write-Host "elevated_exit=$($process.ExitCode) log=$LogPath"
    exit $process.ExitCode
}

New-Item -ItemType Directory -Force -Path (Split-Path $LogPath -Parent) | Out-Null
Start-Transcript -LiteralPath $LogPath -Force | Out-Null
$source = Join-Path $repo 'artifacts\layouts\local\hooklab-watcher'
$watcher = Join-Path $source 'HookLab.Watcher.exe'
$installed = Join-Path $env:LOCALAPPDATA 'Programs\HookLab.Watcher\HookLab.Watcher.exe'
$state = Join-Path $env:LOCALAPPDATA 'HookLab'
$statusPath = Join-Path $state 'watcher-status.json'
$taskTool = Join-Path $env:SystemRoot 'System32\schtasks.exe'
$targetIdentities = @()
$fixture = $null

function Read-Status {
    if (-not (Test-Path -LiteralPath $statusPath)) { return $null }
    Get-Content -LiteralPath $statusPath -Raw | ConvertFrom-Json
}
function Wait-Healthy([int]$PreviousPid = 0,[int]$TimeoutSeconds = 25) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $value = Read-Status
        if ($value -and $value.lifecycle -eq 'running' -and $value.watcherProcessId -ne $PreviousPid) {
            $process = Get-Process -Id $value.watcherProcessId -ErrorAction SilentlyContinue
            if ($process -and $process.Path -eq $installed -and $process.StartTime.ToUniversalTime().Ticks -eq $value.watcherProcessCreationUtcTicks -and $value.catalogGeneration -match '^[0-9a-f]{64}$' -and -not $value.catalogError) { return $value }
        }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'Watcher did not reach exact healthy installed state.'
}
function Invoke-Install([string]$InstallSource = $source,[switch]$ExpectFailure) {
    $output = & $watcher install --source $InstallSource 2>&1
    $exitCode = $LASTEXITCODE
    $output | ForEach-Object { Write-Host $_ }
    if ($ExpectFailure) {
        if ($exitCode -eq 0) { throw 'Injected-failure upgrade unexpectedly succeeded.' }
    } elseif ($exitCode -ne 0) { throw "Watcher install failed with exit code $exitCode." }
    $output -join "`n"
}

try {
    $initial = Wait-Healthy
    $initialHash = (Get-FileHash -LiteralPath $installed -Algorithm SHA256).Hash
    $fixturePath = Join-Path $repo 'tests\TestTargets\Milestone1Target\bin\Release\net48\Milestone1Target.exe'
    $fixture = Start-Process -FilePath $fixturePath -ArgumentList '--exit-after-ms','120000','--exit-code','0' -PassThru -WindowStyle Hidden
    $targetIdentities += [pscustomobject]@{ ProcessId=$fixture.Id; CreationTicks=$fixture.StartTime.ToUniversalTime().Ticks }
    foreach ($result in @($initial.lastResults)) {
        if ($result.status -in @('installed','created','adopted') -and (Get-Process -Id $result.processId -ErrorAction SilentlyContinue)) {
            $targetIdentities += [pscustomobject]@{ ProcessId=[int]$result.processId; CreationTicks=[int64]$result.processCreationUtcTicks }
        }
    }
    Write-Host "initial_pid=$($initial.watcherProcessId) initial_generation=$($initial.catalogGeneration) live_targets=$($targetIdentities.Count)"

    $runningOutput = Invoke-Install
    $running = Wait-Healthy -PreviousPid $initial.watcherProcessId
    if ($runningOutput -notmatch '"running":true') { throw 'Running upgrade did not report restored running intent.' }
    Write-Host "running_upgrade_pid=$($running.watcherProcessId) generation=$($running.catalogGeneration)"

    & $taskTool /End /TN 'HookLab Watcher' | Out-Host
    foreach ($process in @(Get-Process -Name 'HookLab.Watcher' -ErrorAction SilentlyContinue)) {
        if ($process.Path -eq $installed) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
    }
    Start-Sleep -Seconds 1
    $stoppedPid = $running.watcherProcessId
    if (Get-Process -Id $stoppedPid -ErrorAction SilentlyContinue) { throw 'Scheduled watcher did not stop.' }
    $stoppedOutput = Invoke-Install
    Start-Sleep -Seconds 2
    if ($stoppedOutput -notmatch '"running":false') { throw 'Stopped upgrade did not preserve intentionally stopped intent.' }
    $stoppedStatus = Read-Status
    if ($stoppedStatus.watcherProcessId -ne $stoppedPid -or (Get-Process -Id $stoppedStatus.watcherProcessId -ErrorAction SilentlyContinue)) { throw 'Stopped upgrade published a new live watcher.' }
    Write-Host "stopped_upgrade_preserved_pid=$stoppedPid"

    & $taskTool /Run /TN 'HookLab Watcher' | Out-Host
    $beforeFailure = Wait-Healthy -PreviousPid $stoppedPid
    $beforeFailureHash = (Get-FileHash -LiteralPath $installed -Algorithm SHA256).Hash
    $failureLayout = Join-Path $env:TEMP ('hooklab-road1-failure-' + [Guid]::NewGuid().ToString('N'))
    $failureSource = Join-Path $failureLayout 'hooklab-watcher'
    Copy-Item -LiteralPath $source -Destination $failureSource -Recurse
    Copy-Item -LiteralPath (Join-Path $env:SystemRoot 'System32\where.exe') -Destination (Join-Path $failureSource 'HookLab.Watcher.exe') -Force
    $files = @(Get-ChildItem -LiteralPath $failureSource -File -Recurse | ForEach-Object {
        [pscustomobject]@{ path=('hooklab-watcher/' + $_.FullName.Substring($failureSource.Length + 1).Replace('\','/')); size=$_.Length; sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(); owner='hooklab-watcher' }
    })
    [IO.File]::WriteAllText((Join-Path $failureLayout 'dgspy-layout.json'),([pscustomobject]@{ formatVersion=1; files=$files } | ConvertTo-Json -Depth 5 -Compress))
    $failureOutput = Invoke-Install -InstallSource $failureSource -ExpectFailure
    $rolledBack = Wait-Healthy -PreviousPid $beforeFailure.watcherProcessId
    if ((Get-FileHash -LiteralPath $installed -Algorithm SHA256).Hash -ne $beforeFailureHash) { throw 'Injected failure did not restore the prior installed image.' }
    if ($failureOutput -notmatch 'operation_failed') { throw 'Injected failure did not return a stable operation failure.' }

    foreach ($identity in $targetIdentities) {
        $process = Get-Process -Id $identity.ProcessId -ErrorAction SilentlyContinue
        if (-not $process -or $process.StartTime.ToUniversalTime().Ticks -ne $identity.CreationTicks) { throw "Target identity $($identity.ProcessId) was not preserved." }
    }
    Write-Host "rollback_pid=$($rolledBack.watcherProcessId) restored_hash=$beforeFailureHash preserved_targets=$($targetIdentities.Count)"
    Write-Host 'ROAD1_LIVE_PASS'
}
finally {
    if ($failureLayout -and (Test-Path -LiteralPath $failureLayout)) { Remove-Item -LiteralPath $failureLayout -Recurse -Force }
    try {
        $current = Read-Status
        if (-not $current -or $current.lifecycle -ne 'running' -or -not (Get-Process -Id $current.watcherProcessId -ErrorAction SilentlyContinue)) { & $taskTool /Run /TN 'HookLab Watcher' | Out-Host }
    } catch { Write-Warning "Could not restore initial running watcher state: $($_.Exception.Message)" }
    if ($fixture -and -not $fixture.HasExited) { Stop-Process -Id $fixture.Id -Force }
    Stop-Transcript | Out-Null
}
