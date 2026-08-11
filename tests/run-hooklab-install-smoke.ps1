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
	[switch]$ResidentInitializeOnly,
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
$configuredLayout = [Environment]::GetEnvironmentVariable('DGSPY_LAYOUT_ROOT')
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
function Expect-RpcFailure([string]$Label, [hashtable]$Arguments, [string]$ExpectedText) {
    try { $null = Rpc 'install_hook' $Arguments 70; Check $Label $false 'request unexpectedly succeeded' }
    catch { Check $Label ($_.Exception.Message -like ('*' + $ExpectedText + '*')) $_.Exception.Message }
}
function Get-UiElement([int]$ProcessId, [string]$Name, [int]$TimeoutSeconds = 15) {
    if (-not ('System.Windows.Automation.AutomationElement' -as [type])) {
        Add-Type -AssemblyName UIAutomationClient
        Add-Type -AssemblyName UIAutomationTypes
    }
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $scope = [System.Windows.Automation.TreeScope]::Descendants
    $processCondition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty,$ProcessId)
    $nameCondition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty,$Name)
    $condition = New-Object System.Windows.Automation.AndCondition($processCondition,$nameCondition)
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $found = $root.FindFirst($scope,$condition)
        if ($found) { return $found }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $deadline)
    return $null
}
function Invoke-UiElement($Element) {
    $pattern = $null
    if (-not $Element.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern,[ref]$pattern)) { throw "UI element '$($Element.Current.Name)' is not invokable" }
    ([System.Windows.Automation.InvokePattern]$pattern).Invoke()
}
function Open-HookLabAndRemove([int]$HostProcessId, [string]$HookId) {
    Add-Type -AssemblyName UIAutomationClient
    Add-Type -AssemblyName UIAutomationTypes
    $hooks = Get-UiElement $HostProcessId 'Installed hooks'
    $events = Get-UiElement $HostProcessId 'Hook events'
    Check 'HookLab GUI opens with installed-hooks and event lists' ($null -ne $hooks -and $null -ne $events)
    $hookRow = Get-UiElement $HostProcessId $HookId
    Check 'HookLab GUI shows the MCP-installed hook' ($null -ne $hookRow) ("hook_id=" + $HookId)
    $eventRows = if ($events) { @($events.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition)) } else { @() }
    Check 'HookLab GUI receives events without an MCP read driving it' ($eventRows.Count -gt 0) ("descendants=" + $eventRows.Count)
    if ($hookRow) {
        $selection = $null
        $selectable = $hookRow
        $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
        while ($selectable -and -not $selectable.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern,[ref]$selection)) { $selectable = $walker.GetParent($selectable) }
        if (-not $selection) { throw 'HookLab hook row did not expose a selectable ancestor' }
        ([System.Windows.Automation.SelectionItemPattern]$selection).Select()
        Start-Sleep -Milliseconds 500
    }
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $conditions = @(
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty,$HostProcessId)),
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty,'Remove selected HookLab hook')),
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::Button))
    )
    $remove = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,(New-Object System.Windows.Automation.AndCondition($conditions)))
    if (-not $remove) { throw 'HookLab Remove button was not present' }
    Invoke-UiElement $remove
}
function Invoke-HookLabRemoveAll([int]$HostProcessId) {
    $button = Get-UiElement $HostProcessId 'Remove all HookLab hooks'
    if (-not $button) { throw 'HookLab Remove All button was not present' }
    Invoke-UiElement $button
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

# Reflection exposes the original method-body IL byte array; no compiler-output helper is needed.
function Get-MethodFacts([string]$AssemblyPath, [string]$TypeName, [string]$MethodName) {
    $assembly = [Reflection.Assembly]::LoadFrom($AssemblyPath)
    $type = $assembly.GetType($TypeName,$true)
    $method = $type.GetMethod($MethodName,[Reflection.BindingFlags]'Static,NonPublic')
    $il = $method.GetMethodBody().GetILAsByteArray()
    if (-not $il) { throw "reflection did not find a body for $TypeName.$MethodName" }
        $sha = [Security.Cryptography.SHA256]::Create()
        try { $digest = [BitConverter]::ToString($sha.ComputeHash($il)).Replace('-','').ToLowerInvariant() }
        finally { $sha.Dispose() }
        [pscustomobject]@{
            Token = [uint32]$method.MetadataToken
            Mvid = $method.Module.ModuleVersionId.ToString('D')
            Signature = $method.ReturnType.FullName + ' ' + $MethodName + '(' + (($method.GetParameters() | ForEach-Object { $_.ParameterType.FullName }) -join ',') + ')'
            IlSha256 = $digest
        }
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
    $tickFacts = Get-MethodFacts $targetExe 'Milestone1Target.Program' 'Tick'
    $otherFacts = Get-MethodFacts $targetExe 'Milestone1Target.Program' 'UseWorker'
    $carrierFacts = Get-MethodFacts $targetExe 'Milestone1Target.Program' 'ViaInMemory'
    Say "offline dnlib facts token=$($tickFacts.Token) mvid=$($tickFacts.Mvid) il_sha256=$($tickFacts.IlSha256)"

    $hostId = & (Join-Path $PSScriptRoot 'TestSupport\Start-DgSpyHost.ps1') -RpcPort $RpcPort -TargetFramework $TargetFramework
    $RpcPort = [int]$env:DGSPY_RPC_PORT
    Say "host pid=$hostId rpc_port=$RpcPort"

	if ($ResidentInitializeOnly) {
		Say 'resident initialization and first pipe-only hook'
		$fixture = Start-Fixture 'resident-initialize' 90000
		$sessionId = Attach-Fixture $fixture
		try {
			$before = Rpc 'get_session_state' @{ session_id=$sessionId }
			Check 'fixture is running before initialization' ($before.state -eq 'running') ("state=" + $before.state)
			$initialized = Rpc 'initialize_hooklab' @{ session_id=$sessionId; process_id=$fixture.Id } 130
			Check 'initialize_hooklab reports a ready zero-hook runtime' ($initialized.initialized -eq $true -and $initialized.state -eq 'ready') ("changed=" + $initialized.changed + " state=" + $initialized.state)
			$again = Rpc 'initialize_hooklab' @{ session_id=$sessionId; process_id=$fixture.Id } 10
			Check 'initialize_hooklab is idempotent' ($again.initialized -eq $true -and $again.changed -eq $false) ("changed=" + $again.changed)
			$status = Rpc 'get_hooklab_status' @{ session_id=$sessionId; process_id=$fixture.Id }
			Check 'get_hooklab_status sees the retained runtime with zero hooks' ($status.initialized -eq $true -and @($status.runtimes).Count -eq 1) ("runtimes=" + @($status.runtimes).Count)
			$after = Rpc 'get_session_state' @{ session_id=$sessionId }
			Check 'initialization restores the running state' ($after.state -eq 'running') ("state=" + $after.state)

			$installed = Rpc 'install_hook' @{
				session_id=$sessionId; process_id=$fixture.Id; hook_id='resident-tick-prefix'; kind='Prefix'
				module_id=$script:carrierModuleId; assembly='Milestone1Target'; declaring_type='Milestone1Target.Program'
				method='Tick'; method_token=[int]$tickFacts.Token; signature=$tickFacts.Signature
				module_mvid=$tickFacts.Mvid; il_sha256=$tickFacts.IlSha256
			} 70
			Check 'the first hook installs through the resident pipe' ($installed.installed -eq $true -and -not [string]::IsNullOrWhiteSpace($installed.hook.patch_id)) ("patch_id=" + $installed.hook.patch_id)
			Start-Sleep -Milliseconds 1200
			$read = Rpc 'get_hook_events' @{ session_id=$sessionId; process_id=$fixture.Id; after_cursor=0; max_events=64 } 10
			Check 'the pipe-only first hook produces events' (@($read.events).Count -gt 0) ("events=" + @($read.events).Count + " dropped=" + $read.dropped)
			$removed = Rpc 'remove_hook' @{ session_id=$sessionId; process_id=$fixture.Id; hook_id='resident-tick-prefix' } 10
			Check 'the pipe-only hook removes while the target runs' ($removed.removed -eq $true) ("removed=" + $removed.removed)
		}
		finally {
			try { Detach-Fixture $sessionId $fixture $false } catch { Say ("cleanup detach failed for resident initialize: " + $_.Exception.Message) }
		}
		if ($fail -ne 0) { throw "HookLab resident initialization smoke failed: $fail failed, $pass passed. See $driverLog" }
		Say "RESULT pass=$pass fail=$fail"
		return
	}

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
    $fixture = Start-Fixture 'positive' 90000
    $sessionId = Attach-Fixture $fixture
    try {
        $carrierOffset = [uint32]$script:resolvedCarrierOffset
        Say "carrier token=$($carrierFacts.Token) sequence_point=$carrierOffset"
        $stdoutBefore = Get-Content -LiteralPath $fixture.Out -Raw
        $installArguments = @{
            session_id = $sessionId; process_id = $fixture.Id; hook_id = 'tick-prefix'
            kind = 'Prefix'; module_id = $script:carrierModuleId; assembly = 'Milestone1Target'
            declaring_type = 'Milestone1Target.Program'; method = 'Tick'; method_token = [int]$tickFacts.Token
            signature = $tickFacts.Signature; module_mvid = $tickFacts.Mvid; il_sha256 = $tickFacts.IlSha256
            arrival_module_id = $script:carrierModuleId; arrival_method_token = [int]$carrierFacts.Token
            arrival_il_offset = [int]$carrierOffset
        }
        $installWatch = [Diagnostics.Stopwatch]::StartNew()
        $installed = Rpc 'install_hook' $installArguments 130
        $installWatch.Stop()
        Say "MEASURE install_hook_ms=$($installWatch.ElapsedMilliseconds)"
        Check 'public install_hook installs the guarded hook' ($installed.installed -eq $true -and -not [string]::IsNullOrWhiteSpace($installed.hook.patch_id)) ("patch_id=" + $installed.hook.patch_id)
        foreach ($kind in @('Postfix','Finalizer')) {
            $additional = @{}
            foreach ($key in $installArguments.Keys) { $additional[$key] = $installArguments[$key] }
            $additional.kind = $kind
            $additional.hook_id = 'tick-' + $kind.ToLowerInvariant()
            $extra = Rpc 'install_hook' $additional 70
            Check "public install_hook installs $kind" ($extra.installed -eq $true -and $extra.hook.kind -eq $kind) ("patch_id=" + $extra.hook.patch_id)
        }
        foreach ($case in @(
            @{ Label='public install_hook rejects wrong MVID'; Field='module_mvid'; Value=[Guid]::NewGuid().ToString('D'); Text='MVID' },
            @{ Label='public install_hook rejects wrong token'; Field='method_token'; Value=[int]$otherFacts.Token; Text='method' },
            @{ Label='public install_hook rejects wrong signature'; Field='signature'; Value='System.Int32 Tick(System.String)'; Text='signature' },
            @{ Label='public install_hook rejects wrong IL hash'; Field='il_sha256'; Value=('0' * 64); Text='il_sha256' }
        )) {
            $bad = @{}
            foreach ($key in $installArguments.Keys) { $bad[$key] = $installArguments[$key] }
            $bad.hook_id = 'bad-' + $case.Field
            $bad[$case.Field] = $case.Value
            Expect-RpcFailure $case.Label $bad $case.Text
        }
        $listed = Rpc 'list_hooks' @{ session_id=$sessionId; process_id=$fixture.Id }
        Check 'public list_hooks returns prefix, postfix, and finalizer' (@($listed.hooks).Count -eq 3) ("count=" + @($listed.hooks).Count)

        $modules = @((Rpc 'list_modules' @{ session_id=$sessionId; count=500 }).modules)
        $bootstrapModules = @($modules | Where-Object { $_.name -eq 'HookLab.Bootstrap' -or $_.filename -like '*HookLab.Bootstrap*' })
        Check 'list_modules independently sees HookLab.Bootstrap' ($bootstrapModules.Count -gt 0) ("matches=" + $bootstrapModules.Count)
        foreach ($name in @('HookLab.Probe.CorDebug','HookLab.Contracts','0Harmony','HarmonySharedState')) {
            $found = @($modules | Where-Object { ($_.name -eq $name -or $_.filename -like "*$name*") -and $_.is_in_memory })
            Check "list_modules independently sees in-memory $name" ($found.Count -gt 0) ("matches=" + $found.Count)
        }

        Start-Sleep -Seconds 2
        $eventArguments = @{ session_id=$sessionId; process_id=$fixture.Id; max_events=256; after_cursor=0 }
        $drain = Rpc 'get_hook_events' $eventArguments 70
        Check 'public get_hook_events sees more than zero events' (@($drain.events).Count -gt 0) ("count=" + @($drain.events).Count)
        Check 'public get_hook_events reports no drops' ([int64]$drain.dropped -eq 0) ("dropped=" + $drain.dropped)
        $eventText = (@($drain.events | ForEach-Object { $_.patch_id + '|' + $_.payload_json }) -join ' ')
        Check 'public events name the hooked Tick method' ($eventText -like '*Tick*') $eventText
        $stdoutAfter = Get-Content -LiteralPath $fixture.Out -Raw
        Check 'the recording hook leaves fixture stdout unchanged' ($stdoutAfter -ceq $stdoutBefore) ("before=" + ($stdoutBefore -replace "`r?`n",'|') + " after=" + ($stdoutAfter -replace "`r?`n",'|'))

        Open-HookLabAndRemove $hostId 'tick-prefix'
        $removeDeadline = [DateTime]::UtcNow.AddSeconds(70)
        do {
            Start-Sleep -Milliseconds 200
            $listedAfter = Rpc 'list_hooks' @{ session_id=$sessionId; process_id=$fixture.Id }
        } while (@($listedAfter.hooks).Count -ne 2 -and [DateTime]::UtcNow -lt $removeDeadline)
        Check 'GUI Remove removes one owned patch through the shared service' (@($listedAfter.hooks).Count -eq 2) ("count=" + @($listedAfter.hooks).Count)
        Invoke-HookLabRemoveAll $hostId
        do {
            Start-Sleep -Milliseconds 200
            $listedAfter = Rpc 'list_hooks' @{ session_id=$sessionId; process_id=$fixture.Id }
        } while (@($listedAfter.hooks).Count -ne 0 -and [DateTime]::UtcNow -lt $removeDeadline)
        Check 'GUI Remove All removes the remaining owned patches' (@($listedAfter.hooks).Count -eq 0) ("count=" + @($listedAfter.hooks).Count)
        $listedAfter = Rpc 'list_hooks' @{ session_id=$sessionId; process_id=$fixture.Id }
        Check 'public list_hooks is empty after removal' (@($listedAfter.hooks).Count -eq 0) ("count=" + @($listedAfter.hooks).Count)
        $removalBoundary = Rpc 'get_hook_events' @{ session_id=$sessionId; process_id=$fixture.Id; max_events=256; after_cursor=$drain.next_cursor } 70
        Start-Sleep -Seconds 1
        $afterRemoval = Rpc 'get_hook_events' @{ session_id=$sessionId; process_id=$fixture.Id; max_events=256; after_cursor=$removalBoundary.next_cursor } 70
        Check 'public event read beyond the removal boundary sees zero new events' (@($afterRemoval.events).Count -eq 0) ("count=" + @($afterRemoval.events).Count)
        Detach-Fixture $sessionId $fixture $false
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
