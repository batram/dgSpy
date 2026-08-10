#requires -Version 5.1
<#
.SYNOPSIS
    Live HookLab install, event, removal, and refusal smoke.

.DESCRIPTION
    Drives the prototype through start_atomic_action against an unoptimized net48 target. Preparation
    and commit are separate action runs with a real resume between them. Commit completion is read from
    its file, never by introducing another debugger stop.

    ASCII-only by policy. GUI-launching, so run this script on Invoke-OnHiddenDesktop.ps1 and redirect
    this script's output to a log file owned by the hidden-desktop child.
#>
[CmdletBinding()]
param(
    [ValidateSet('net10.0-windows','net48')][string]$TargetFramework = 'net10.0-windows',
    [int]$RpcPort = 0,
    [string]$RunDirectory,
    [switch]$FixtureChild,
    [string]$FixtureExitPath,
    [int]$FixtureExitAfterMs
)

$ErrorActionPreference = 'Stop'
$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new()
$repoRoot = Split-Path $PSScriptRoot -Parent
$logRoot = if ([string]::IsNullOrWhiteSpace($env:DGSPY_SMOKE_LOG_ROOT)) { Join-Path $PSScriptRoot 'artifacts' } else { $env:DGSPY_SMOKE_LOG_ROOT }
if ([string]::IsNullOrWhiteSpace($RunDirectory)) { $RunDirectory = Join-Path $logRoot ('dgspy-smoke-hooklab-install-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
$targetExe = Join-Path $repoRoot 'tests\TestTargets\Milestone1Target\bin\Release\net48\Milestone1Target.exe'
$fixtureChildMode = $FixtureChild
if ($fixtureChildMode) {
    & $targetExe '--exit-after-ms' "$FixtureExitAfterMs" '--exit-code' '0'
    $nativeExitCode = $LASTEXITCODE
    [IO.File]::WriteAllText($FixtureExitPath,"$nativeExitCode")
    exit $nativeExitCode
}
$dnlibPath = Join-Path $repoRoot 'Build\compiled\dnlib.dll'
$driverLog = Join-Path $RunDirectory 'driver.log'
$hostId = 0
$pass = 0
$fail = 0
$startedProcesses = New-Object System.Collections.ArrayList

. (Join-Path $PSScriptRoot 'TestSupport\Invoke-DgSpyRpc.ps1')

function Say([string]$Text) {
    $line = (Get-Date -Format 'HH:mm:ss.fff') + '  ' + $Text
    Write-Host $line
    Add-Content -LiteralPath $driverLog -Value $line -Encoding utf8
}
function Check([string]$Name, [bool]$Ok, [string]$Detail = '') {
    if ($Ok) { $script:pass++; Say ('PASS  ' + $Name + $(if ($Detail) { '  ' + $Detail } else { '' })) }
    else { $script:fail++; Say ('FAIL  ' + $Name + '  ' + $Detail) }
}
function Rpc([string]$Operation, [hashtable]$Arguments = @{}, [int]$Deadline = 30) {
    Invoke-DgSpyRpc -OperationName $Operation -OperationArguments $Arguments -RpcPort $RpcPort -DeadlineSeconds $Deadline
}
function Parse-Report([string]$Text) {
    $report = @{}
    foreach ($line in @($Text -split "`r?`n")) {
        $separator = $line.IndexOf('=')
        if ($separator -gt 0) { $report[$line.Substring(0,$separator)] = $line.Substring($separator + 1) }
    }
    $report
}
function Wait-File([string]$Path, [int]$TimeoutSeconds = 20) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (Test-Path -LiteralPath $Path) { return Get-Content -LiteralPath $Path -Raw }
        Start-Sleep -Milliseconds 50
    }
    throw "completion file was not published within $TimeoutSeconds seconds: $Path"
}
function Wait-Action([string]$ActionId, [int]$TimeoutSeconds = 40) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $status = Rpc 'get_atomic_action_status' @{ operation_version=1; action_id=$ActionId }
        if ($status.completed) {
            if (-not $status.result) { throw "action $ActionId completed without a result: $($status | ConvertTo-Json -Compress -Depth 8)" }
            return $status.result
        }
        Start-Sleep -Milliseconds 100
    }
    throw "atomic action $ActionId did not reach a terminal record"
}
function Start-PayloadAction([string]$SessionId, [int]$TargetId, [uint32]$CarrierToken, [uint32]$CarrierOffset, [string]$Operation, [hashtable]$PayloadArguments = @{}, [uint32[]]$NearbyOffsets = @()) {
    $actionId = [Guid]::NewGuid().ToString('N')
    $arguments = @{
        operation_version = 1
        session_id = $SessionId
        process_id = $TargetId
        module_id = $script:carrierModuleId
        action_kind = 'payload'
        timeout_ms = 30000
        request = @{
            schema_version = 1
            action_id = $actionId
            action_name = 'HookLab ' + $Operation
            process_id = $TargetId
            runtime_id = ''
            app_domain_id = ''
            module = $script:carrierModuleId
            method_token = [int]$CarrierToken
            il_offset = [int]$CarrierOffset
            nearby_offsets = @($NearbyOffsets | ForEach-Object { [int]$_ })
            resume_policy = 'resume'
        }
        payload_operation = $Operation
    }
    foreach ($key in $PayloadArguments.Keys) { $arguments[$key] = $PayloadArguments[$key] }
    $accepted = Rpc 'start_atomic_action' $arguments 30
    Check "$Operation was accepted asynchronously" ($accepted.accepted -eq $true -and $accepted.action_id -eq $actionId) ("accepted=" + $accepted.accepted + " phase=" + $accepted.phase)
    Wait-Action $actionId
}
function Check-Action([string]$Label, $Result, [uint32]$CarrierToken, [uint32]$CarrierOffset) {
    Check "$Label completed" ($Result.action_outcome -eq 'completed') ("outcome=" + $Result.action_outcome + " error=" + $Result.error)
    Check "$Label used the requested slot" ($Result.used_slot -and [int]$Result.used_slot.method_token -eq [int]$CarrierToken -and [int]$Result.used_slot.il_offset -eq [int]$CarrierOffset) ("slot=" + ($Result.used_slot | ConvertTo-Json -Compress))
    Check "$Label had no evaluation blocker" ($Result.evaluation_blocker -eq 'none') ("blocker=" + $Result.evaluation_blocker + " probe=" + $Result.evaluation_probe_error)
    Check "$Label resumed the target" ($Result.final_debugger_state.is_running -eq $true) ("running=" + $Result.final_debugger_state.is_running)
}

# dnlib selects the exact MethodDef and maps its RVA. The small PE reader hashes the method body's raw
# IL bytes, not a reflection result from the target and not an IL re-encoding that could normalize bytes.
function Get-DnlibMethodFacts([string]$AssemblyPath, [string]$TypeName, [string]$MethodName) {
    if (-not ('dnlib.DotNet.ModuleDefMD' -as [type])) { [Reflection.Assembly]::LoadFrom($dnlibPath) | Out-Null }
    $module = [dnlib.DotNet.ModuleDefMD]::Load($AssemblyPath)
    try {
        $type = @($module.Types | Where-Object { $_.FullName -eq $TypeName })[0]
        $method = @($type.Methods | Where-Object { $_.Name.String -eq $MethodName })[0]
        if (-not $method -or -not $method.HasBody) { throw "dnlib did not find a body for $TypeName.$MethodName" }
        $bytes = [IO.File]::ReadAllBytes($AssemblyPath)
        $offset = [int]$module.Metadata.PEImage.ToFileOffset($method.RVA)
        $first = [int]$bytes[$offset]
        if (($first -band 3) -eq 2) { $header = 1; $size = $first -shr 2 }
        elseif (($first -band 3) -eq 3) {
            $flagsAndSize = [BitConverter]::ToUInt16($bytes,$offset)
            $header = (($flagsAndSize -shr 12) -band 15) * 4
            $size = [BitConverter]::ToInt32($bytes,$offset + 4)
        }
        else { throw "unsupported method header 0x$('{0:X2}' -f $first) at file offset $offset" }
        $il = New-Object byte[] $size
        [Array]::Copy($bytes,$offset + $header,$il,0,$size)
        $sha = [Security.Cryptography.SHA256]::Create()
        try { $digest = [BitConverter]::ToString($sha.ComputeHash($il)).Replace('-','').ToLowerInvariant() }
        finally { $sha.Dispose() }
        [pscustomobject]@{
            Token = [uint32]$method.MDToken.Raw
            Mvid = $module.Mvid.ToString('D')
            Signature = 'System.Int32 ' + $MethodName + '(System.Int32)'
            IlSha256 = $digest
        }
    }
    finally { $module.Dispose() }
}
function Start-Fixture([string]$Label, [int]$ExitAfterMs) {
    $stdout = Join-Path $RunDirectory ($Label + '.out')
    $stderr = Join-Path $RunDirectory ($Label + '.err')
    $exitPath = Join-Path $RunDirectory ($Label + '.exit-code')
    $process = Start-Process -FilePath "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" `
        -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-File',('"' + $PSCommandPath + '"'),'-FixtureChild','-FixtureExitPath',('"' + $exitPath + '"'),'-FixtureExitAfterMs',"$ExitAfterMs" `
        -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    $null = $startedProcesses.Add($process)
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        Start-Sleep -Milliseconds 50
        $lines = @(Get-Content -LiteralPath $stdout -ErrorAction SilentlyContinue)
    } until (($lines | Where-Object { $_ -match '^PID=\d+$' }) -and ($lines | Where-Object { $_ -match '^TOKEN=\d+$' }) -or $process.HasExited -or [DateTime]::UtcNow -gt $deadline)
    $pidLine = $lines | Where-Object { $_ -match '^PID=\d+$' } | Select-Object -First 1
    $tokenLine = $lines | Where-Object { $_ -match '^TOKEN=\d+$' } | Select-Object -First 1
    if (-not $pidLine -or -not $tokenLine) { throw "${Label} did not publish PID and TOKEN; stderr=$(@(Get-Content -LiteralPath $stderr -ErrorAction SilentlyContinue) -join ' | ')" }
    $targetId = [int]($pidLine -replace '^PID=','')
    $targetProcess = Get-Process -Id $targetId -ErrorAction Stop
    $null = $startedProcesses.Add($targetProcess)
    [pscustomobject]@{ Process=$process; TargetProcess=$targetProcess; Id=$targetId; Token=[uint32]($tokenLine -replace '^TOKEN=',''); Out=$stdout; Err=$stderr; ExitPath=$exitPath }
}
function Attach-Fixture($Fixture) {
    $program = @(Rpc 'list_programs' @{ process_ids=@($Fixture.Id) })[0]
    if (-not $program) { throw "target pid $($Fixture.Id) was not discoverable" }
    $sessionId = (Rpc 'attach' @{ program_id=$program.program_id } 60).session_id
    $targetModule = @((Rpc 'list_modules' @{ session_id=$sessionId; name_pattern='Milestone1Target' }).modules | Where-Object { $_.filename -like '*Milestone1Target.exe' } | Select-Object -First 1)[0]
    if (-not $targetModule -or [string]::IsNullOrWhiteSpace($targetModule.module_id)) { throw "list_modules did not publish Milestone1Target's module_id" }
    $script:carrierModuleId = $targetModule.module_id
    $sessionId
}
function Get-CarrierOffset([string]$SessionId, [uint32]$CarrierToken) {
    $il = Rpc 'get_il' @{ session_id=$SessionId; module=$targetExe; module_id=$script:carrierModuleId; method_token=[int]$CarrierToken }
    $point = @($il.instructions | Where-Object { $_.is_sequence_point } | Select-Object -First 1)[0]
    if (-not $point) { throw "get_il found no sequence point for carrier token $CarrierToken" }
    [uint32]$point.offset
}
function Get-CarrierOffsets([string]$SessionId, [uint32]$CarrierToken) {
    $il = Rpc 'get_il' @{ session_id=$SessionId; module=$targetExe; module_id=$script:carrierModuleId; method_token=[int]$CarrierToken }
    [uint32[]]@($il.instructions | ForEach-Object { [uint32]$_.offset })
}
function Detach-Fixture([string]$SessionId, $Fixture, [bool]$ExpectCleanExit) {
    $state = Rpc 'get_session_state' @{ session_id=$SessionId }
    if ($state.state -eq 'paused') { $null = Rpc 'continue' @{ session_id=$SessionId } }
    $detached = Rpc 'detach' @{ session_id=$SessionId } 60
    Check 'detach leaves the target running' ($detached.detached -eq $true -and $detached.terminated -ne $true -and $null -ne (Get-Process -Id $Fixture.Id -ErrorAction SilentlyContinue)) ("detached=" + $detached.detached + " terminated=" + $detached.terminated)
    if ($ExpectCleanExit) {
        $Fixture.Process.WaitForExit(30000) | Out-Null
        $exitCode = if (Test-Path -LiteralPath $Fixture.ExitPath) { [int](Get-Content -LiteralPath $Fixture.ExitPath -Raw) } else { $null }
        Check 'the detached target exits cleanly' ($Fixture.Process.HasExited -and $null -eq (Get-Process -Id $Fixture.Id -ErrorAction SilentlyContinue) -and $exitCode -eq 0) ("launcher_exited=" + $Fixture.Process.HasExited + " target_running=" + ($null -ne (Get-Process -Id $Fixture.Id -ErrorAction SilentlyContinue)) + " code=" + $exitCode)
    }
}
function Hook-Parameters($Fixture, $Facts, [string]$CompletionPath) {
    @{
        host_id = 'hooklab-install-smoke'
        image_path = $targetExe
        process_creation_utc_ticks = $Fixture.TargetProcess.StartTime.ToUniversalTime().Ticks.ToString()
        architecture = 'x64'
        runtime_id = 'v4.0.30319'
        endpoint = 'none'
        completion_path = $CompletionPath
        hook_id = 'Milestone1Target.Program.Tick'
        hook_kind = 'Prefix'
        hook_assembly = 'Milestone1Target'
        hook_type = 'Milestone1Target.Program'
        hook_method = 'Tick'
        hook_module_mvid = $Facts.Mvid
        hook_metadata_token = $Facts.Token.ToString()
        hook_declaring_type = 'Milestone1Target.Program'
        hook_method_signature = $Facts.Signature
        hook_il_sha256 = $Facts.IlSha256
    }
}
function Run-Negative([string]$Label, [scriptblock]$Mutate, [scriptblock]$Assert) {
    Say "negative $Label"
    $fixture = Start-Fixture $Label 15000
    $sessionId = Attach-Fixture $fixture
    try {
        $carrierOffset = if ($null -ne $script:resolvedCarrierOffset) { [uint32]$script:resolvedCarrierOffset } else { Get-CarrierOffset $sessionId $carrierFacts.Token }
        $nearby = if ($null -eq $script:resolvedCarrierOffset) { Get-CarrierOffsets $sessionId $carrierFacts.Token } else { @() }
        $completion = Join-Path $RunDirectory ($Label + '.completion')
        $parameters = Hook-Parameters $fixture $tickFacts $completion
        & $Mutate $parameters
        $prepare = Start-PayloadAction $sessionId $fixture.Id $carrierFacts.Token $carrierOffset 'prepare' @{ payload_parameters=$parameters } $nearby
        Check "$Label prepare completed" ($prepare.action_outcome -eq 'completed') ("outcome=" + $prepare.action_outcome + " error=" + $prepare.error)
        if ($prepare.used_slot) { $carrierOffset = [uint32]$prepare.used_slot.il_offset; $script:resolvedCarrierOffset = $carrierOffset }
        $commit = Start-PayloadAction $sessionId $fixture.Id $carrierFacts.Token $carrierOffset 'commit'
        Check-Action "$Label commit" $commit $carrierFacts.Token $carrierOffset
        $text = Wait-File $completion
        $report = Parse-Report $text
        & $Assert $report $text
    }
    finally {
        try { Detach-Fixture $sessionId $fixture $true } catch { Say ("cleanup detach failed for ${Label}: " + $_.Exception.Message) }
    }
}

New-Item -ItemType Directory -Force -Path $RunDirectory | Out-Null
try {
    if (-not (Test-Path -LiteralPath $targetExe)) { throw "unoptimized Release/net48 target not built: $targetExe" }
    if (-not (Test-Path -LiteralPath $dnlibPath)) { throw "in-tree dnlib not built: $dnlibPath" }
    $tickFacts = Get-DnlibMethodFacts $targetExe 'Milestone1Target.Program' 'Tick'
    $otherFacts = Get-DnlibMethodFacts $targetExe 'Milestone1Target.Program' 'UseWorker'
    $carrierFacts = Get-DnlibMethodFacts $targetExe 'Milestone1Target.Program' 'ViaInMemory'
    Say "offline dnlib facts token=$($tickFacts.Token) mvid=$($tickFacts.Mvid) il_sha256=$($tickFacts.IlSha256)"

    $hostId = & (Join-Path $PSScriptRoot 'TestSupport\Start-DgSpyHost.ps1') -RpcPort $RpcPort -TargetFramework $TargetFramework
    $RpcPort = [int]$env:DGSPY_RPC_PORT
    Say "host pid=$hostId rpc_port=$RpcPort"

    Run-Negative 'N1-wrong-token' { param($parameters) $parameters.hook_metadata_token = $otherFacts.Token.ToString() } {
        param($report,$text)
        Check 'N1 wrong same-module token is refused' ($report.status -eq 'error') ("report=" + ($text -replace "`r?`n",';'))
        Check 'N1 refusal has no patch id' (-not $report.ContainsKey('patch_id')) ("patch_id=" + $report.patch_id)
    }
    Run-Negative 'N2-wrong-il' { param($parameters) $first = if ($parameters.hook_il_sha256[0] -eq '0') { '1' } else { '0' }; $parameters.hook_il_sha256 = $first + $parameters.hook_il_sha256.Substring(1) } {
        param($report,$text)
        Check 'N2 changed IL digest is refused' ($report.status -eq 'error') ("report=" + ($text -replace "`r?`n",';'))
        Check 'N2 names GuardMismatchException and il_sha256' ($report.error_type -like '*GuardMismatchException*' -and $report.error_message -like '*il_sha256*') ("type=" + $report.error_type + " message=" + $report.error_message)
        Check 'N2 refusal has no patch id' (-not $report.ContainsKey('patch_id')) ("patch_id=" + $report.patch_id)
    }

    Say 'positive install and events'
    $fixture = Start-Fixture 'positive' 30000
    $sessionId = Attach-Fixture $fixture
    try {
        $carrierOffset = [uint32]$script:resolvedCarrierOffset
        Say "carrier token=$($carrierFacts.Token) sequence_point=$carrierOffset"
        $completion = Join-Path $RunDirectory 'positive.completion'
        $parameters = Hook-Parameters $fixture $tickFacts $completion
        $stdoutBefore = Get-Content -LiteralPath $fixture.Out -Raw
        $prepareWatch = [Diagnostics.Stopwatch]::StartNew()
        $prepare = Start-PayloadAction $sessionId $fixture.Id $carrierFacts.Token $carrierOffset 'prepare' @{ payload_parameters=$parameters }
        $prepareWatch.Stop()
        Check-Action 'prepare' $prepare $carrierFacts.Token $carrierOffset
        Say "MEASURE prepare_action_ms=$($prepareWatch.ElapsedMilliseconds)"
        $prepareEvidence = $prepare.verification_evidence | ConvertFrom-Json
        $prepareReport = Parse-Report $prepareEvidence.report
        Check 'prepare report is ok' ($prepareReport.status -eq 'ok') ("status=" + $prepareReport.status)

        $commitWatch = [Diagnostics.Stopwatch]::StartNew()
        $commit = Start-PayloadAction $sessionId $fixture.Id $carrierFacts.Token $carrierOffset 'commit'
        $commitWatch.Stop()
        Check-Action 'commit' $commit $carrierFacts.Token $carrierOffset
        Say "MEASURE commit_action_ms=$($commitWatch.ElapsedMilliseconds)"
        $completionText = Wait-File $completion
        $completionReport = Parse-Report $completionText
        Check 'completion evidence reports ok with a patch id' ($completionReport.status -eq 'ok' -and -not [string]::IsNullOrWhiteSpace($completionReport.patch_id)) ("status=" + $completionReport.status + " patch_id=" + $completionReport.patch_id)
        Check 'backend inventory was captured before Harmony resolved' ($completionReport.backend_inventory_at_initialize -eq '') ("inventory=" + $completionReport.backend_inventory_at_initialize)
        Check 'completion evidence carries no secret_base64' (-not $completionReport.ContainsKey('secret_base64')) ($completionText -replace "`r?`n",';')

        $modules = @((Rpc 'list_modules' @{ session_id=$sessionId; count=500 }).modules)
        foreach ($name in @('HookLab.Bootstrap','HookLab.Probe.CorDebug','HookLab.Contracts','0Harmony','HarmonySharedState')) {
            $found = @($modules | Where-Object { ($_.name -eq $name -or $_.filename -like "*$name*") -and $_.is_in_memory })
            Check "list_modules independently sees in-memory $name" ($found.Count -gt 0) ("matches=" + $found.Count)
        }

        Start-Sleep -Seconds 2
        $drain = Start-PayloadAction $sessionId $fixture.Id $carrierFacts.Token $carrierOffset 'drain' @{ drain_max=256 }
        Check-Action 'drain' $drain $carrierFacts.Token $carrierOffset
        $drainEvidence = $drain.verification_evidence | ConvertFrom-Json
        $drainReport = Parse-Report $drainEvidence.report
        Check 'drain sees more than zero hook events' ([int]$drainReport.count -gt 0) ("count=" + $drainReport.count)
        Check 'drain dropped no events' ([int64]$drainReport.dropped -eq 0) ("dropped=" + $drainReport.dropped)
        $eventText = (@($drainReport.Keys | Where-Object { $_ -like 'event_*' } | ForEach-Object { $drainReport[$_] }) -join ' ')
        Check 'drained events name the hooked Tick method' ($eventText -like '*Tick*') $eventText
        $stdoutAfter = Get-Content -LiteralPath $fixture.Out -Raw
        Check 'the recording hook leaves fixture stdout unchanged' ($stdoutAfter -ceq $stdoutBefore) ("before=" + ($stdoutBefore -replace "`r?`n",'|') + " after=" + ($stdoutAfter -replace "`r?`n",'|'))

        # Binding Step 3 requires this fixed operation. If the product has not published it, this is the
        # deliberate failure rung; do not replace it with a caller-composed evaluate expression.
        $shutdown = Start-PayloadAction $sessionId $fixture.Id $carrierFacts.Token $carrierOffset 'shutdown'
        Check-Action 'shutdown' $shutdown $carrierFacts.Token $carrierOffset
        $shutdownEvidence = $shutdown.verification_evidence | ConvertFrom-Json
        $shutdownReport = Parse-Report $shutdownEvidence.report
        Check 'shutdown reports ok and resident payloads' ($shutdownReport.status -eq 'ok' -and $shutdownReport.payloads_resident -eq 'true') ("status=" + $shutdownReport.status + " resident=" + $shutdownReport.payloads_resident)
        Start-Sleep -Seconds 1
        $afterShutdown = Start-PayloadAction $sessionId $fixture.Id $carrierFacts.Token $carrierOffset 'drain' @{ drain_max=256 }
        $afterEvidence = $afterShutdown.verification_evidence | ConvertFrom-Json
        $afterReport = Parse-Report $afterEvidence.report
        Check 'drain after shutdown sees zero new events' ([int]$afterReport.count -eq 0) ("count=" + $afterReport.count)
        Detach-Fixture $sessionId $fixture $true
        $sessionId = $null
    }
    finally {
        if ($sessionId) { try { $null = Rpc 'detach' @{ session_id=$sessionId } 30 } catch { Say ("positive detach cleanup failed: " + $_.Exception.Message) } }
    }
}
catch {
    $script:fail++
    Say ('FATAL rung=' + $(if ($fixture -and $sessionId) { 'positive-live-sequence' } else { 'setup-or-negative' }) + ' expression=' + $_.InvocationInfo.Line.Trim() + ' error=' + $_.Exception.Message)
}
finally {
    foreach ($process in @($startedProcesses)) {
        try {
            if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
            $process.WaitForExit(5000) | Out-Null
        } catch { }
    }
    if ($hostId) { Stop-Process -Id $hostId -Force -ErrorAction SilentlyContinue }
    Remove-Item Env:DGSPY_RPC_PORT -ErrorAction SilentlyContinue
    $remaining = @($startedProcesses | Where-Object { Get-Process -Id $_.Id -ErrorAction SilentlyContinue })
    Check 'no process started by the smoke remains' ($remaining.Count -eq 0) ("remaining=" + (($remaining | ForEach-Object { $_.Id }) -join ','))
    Say "passed=$pass failed=$fail"
    Say "artifacts=$RunDirectory"
}
if ($fail) { exit 1 }
