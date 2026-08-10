#requires -Version 5.1
<#
.SYNOPSIS
	Live atomic-action smoke: run_atomic_action against a real CorDebug target.

.DESCRIPTION
	T08 shipped with no live leg at all - "verified by deploying and load-checking" is a load check,
	not behaviour - and every one of T08b's nine defects lived in the dnSpy-facing half that no test
	entered. This drives the real operation end to end against Milestone1Target: natural arrival at an
	owned breakpoint, a capture at the frame that arrived, ownership-scoped cleanup, both resume
	policies, the declared-nearby-slot path, and the status/cancel record operations.

	ASCII-only by policy: an em dash in a .ps1 parses as a stray quote under CP1252.
	GUI-launching, so run it in the background on a hidden desktop; it writes everything to a log.
#>
[CmdletBinding()]
param(
    [ValidateSet('net10.0-windows','net48')][string]$TargetFramework = 'net10.0-windows',
    # Not 7351: an installed dgSpy holding that port would make this smoke test the wrong host.
    [int]$RpcPort = 7369,
    [int]$GatewayPort = 7358,
    [string]$RunDirectory
)

$ErrorActionPreference = 'Stop'
$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new()
$repoRoot = Split-Path $PSScriptRoot -Parent
$logRoot = if ([string]::IsNullOrWhiteSpace($env:DGSPY_SMOKE_LOG_ROOT)) { Join-Path $PSScriptRoot 'artifacts' } else { $env:DGSPY_SMOKE_LOG_ROOT }
if ([string]::IsNullOrWhiteSpace($RunDirectory)) { $RunDirectory = Join-Path $logRoot ('dgspy-smoke-atomic-action-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
$targetExe = Join-Path $repoRoot 'tests\TestTargets\Milestone1Target\bin\Release\net48\Milestone1Target.exe'
$driverLog = Join-Path $RunDirectory 'driver.log'
$gatewayDll = Join-Path $repoRoot 'dgSpy.Gateway\bin\Release\net10.0\dgSpy.Gateway.dll'
$gatewayUrl = "http://127.0.0.1:$GatewayPort"
$gatewayToken = [Guid]::NewGuid().ToString('N') + [Guid]::NewGuid().ToString('N')
$targetLauncher = $null
$gatewayProcess = $null
$targetId = 0
$hostId = 0
$pass = 0
$fail = 0

. (Join-Path $PSScriptRoot 'TestSupport\Invoke-DgSpyRpc.ps1')
. (Join-Path $PSScriptRoot 'TestSupport\McpClient.ps1')

function Say([string]$Text) {
    $line = (Get-Date -Format 'HH:mm:ss') + '  ' + $Text
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
function Ensure-Paused([string]$SessionId) {
    $state = Rpc 'get_session_state' @{ session_id=$SessionId }
    if ($state.state -ne 'paused') { $null = Rpc 'pause' @{ session_id=$SessionId } }
}
# One action request, with only the identity the caller can actually discover. runtime_id and
# app_domain_id are deliberately left empty: no read operation reports either value, and the state
# machine treats an omitted one as unconstrained rather than as a mismatch.
function ActionArguments([string]$SessionId, [uint32]$Token, [uint32]$Offset, [string]$ResumePolicy, [uint32[]]$Nearby = @()) {
    @{
        operation_version = 1
        session_id = $SessionId
        process_id = $targetId
        action_kind = 'capture'
        capture_path = 'input'
        timeout_ms = 20000
        request = @{
            schema_version = 1
            action_id = [Guid]::NewGuid().ToString('N')
            action_name = 'smoke capture'
            process_id = $targetId
            runtime_id = ''
            app_domain_id = ''
            module = 'Milestone1Target.exe'
            method_token = [int]$Token
            il_offset = [int]$Offset
            nearby_offsets = @($Nearby | ForEach-Object { [int]$_ })
            resume_policy = $ResumePolicy
            disconnect_policy = 'complete_on_disconnect'
        }
    }
}

# Sends one operation and drops the connection without reading the answer, which is what a client
# disconnect actually looks like to the extension. Invoke-DgSpyRpc cannot express this: it disposes
# its client in a finally block only after reading the response.
function Send-AndDisconnect([string]$Operation, [hashtable]$Arguments) {
    $rpcTokenPath = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'dgSpy\rpc.token'
    $rpcToken = (Get-Content -LiteralPath $rpcTokenPath -Raw).Trim()
    $client = [Net.Sockets.TcpClient]::new()
    $client.Connect('127.0.0.1', $RpcPort)
    $stream = $client.GetStream()
    $reader = [IO.StreamReader]::new($stream, [Text.Encoding]::UTF8, $false, 4096, $true)
    $writer = [IO.StreamWriter]::new($stream, [Text.UTF8Encoding]::new($false), 4096, $true)
    $writer.AutoFlush = $true
    $writer.WriteLine((@{ version=2; request_id=[Guid]::NewGuid().ToString('N'); authentication_token=$rpcToken; operation='ping'; arguments=@{}; deadline_utc=[DateTime]::UtcNow.AddSeconds(3).ToString('O') } | ConvertTo-Json -Compress))
    $ping = $reader.ReadLine() | ConvertFrom-Json
    if ($ping.error) { throw "ping failed: $($ping.error.code)" }
    $writer.WriteLine((@{ version=2; request_id=[Guid]::NewGuid().ToString('N'); host_id=$ping.result.host_id; authentication_token=$rpcToken; operation=$Operation; arguments=$Arguments; deadline_utc=[DateTime]::UtcNow.AddSeconds(120).ToString('O') } | ConvertTo-Json -Compress -Depth 8))
    Start-Sleep -Milliseconds 200
    $client.Close()
    $client.Dispose()
}

function Wait-ActionRecord([string]$ActionId, [int]$TimeoutSeconds = 40) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $status = Rpc 'get_atomic_action_status' @{ operation_version=1; action_id=$ActionId }
        if ($status.completed) { return $status }
        Start-Sleep -Milliseconds 250
    }
    throw "atomic action $ActionId did not reach a terminal record"
}

New-Item -ItemType Directory -Force -Path $RunDirectory | Out-Null
try {
    if (-not (Test-Path -LiteralPath $targetExe)) { throw "CorDebug target not built: $targetExe" }
    if (@(Get-NetTCPConnection -State Listen -LocalPort $RpcPort -ErrorAction SilentlyContinue).Count) { throw "port $RpcPort is already in use" }

    $targetOut = Join-Path $RunDirectory 'target.out'
    $targetErr = Join-Path $RunDirectory 'target.err'
    $env:DGSPY_OWNED_BP_TARGET_EXE = $targetExe
    $env:DGSPY_OWNED_BP_TARGET_OUT = $targetOut
    $env:DGSPY_OWNED_BP_TARGET_ERR = $targetErr
    $targetLauncherScript = Join-Path $PSScriptRoot 'OwnedBreakpoint.LiveTestExtension\Run-TestTarget.ps1'
    $targetLauncher = Start-Process -FilePath "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" `
        -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-File',('"' + $targetLauncherScript + '"') -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    while ([DateTime]::UtcNow -lt $deadline -and @(Get-Content -LiteralPath $targetOut -ErrorAction SilentlyContinue).Count -lt 2) {
        if ($targetLauncher.HasExited) { break }
        Start-Sleep -Milliseconds 100
    }
    $targetLines = @(Get-Content -LiteralPath $targetOut -ErrorAction SilentlyContinue)
    $pidLine = $targetLines | Where-Object { $_ -match '^PID=\d+$' } | Select-Object -First 1
    $tokenLine = $targetLines | Where-Object { $_ -match '^TOKEN=\d+$' } | Select-Object -First 1
    if (-not $pidLine -or -not $tokenLine) {
        $stderrTail = @(Get-Content -LiteralPath $targetErr -Tail 20 -ErrorAction SilentlyContinue) -join ' | '
        throw "target did not publish PID and token; stderr=$stderrTail"
    }
    $targetId = [int]($pidLine -replace '^PID=', '')
    # Tick(int input) runs every loop iteration, so IL offset 0 is reached by natural execution.
    $token = [uint32]($tokenLine -replace '^TOKEN=', '')
    Say "target pid=$targetId tick_token=$token"

    # A method the fixture never calls, so an action targeting it can only ever time out. Read from
    # the built assembly the same way the target reads Tick's own token.
    $neverCalledToken = [uint32]([Reflection.Assembly]::LoadFrom($targetExe).GetType('Milestone1Target.Program').GetMethod('GenericExtensionFixture',[Reflection.BindingFlags]'Static,NonPublic').MetadataToken)
    Say "never-called token=$neverCalledToken"

    if (-not (Test-Path -LiteralPath $gatewayDll)) { throw "Gateway not built: $gatewayDll" }
    $env:DGSPY_URL = $gatewayUrl
    $env:DGSPY_TOKEN = $gatewayToken
    $hostId = & (Join-Path $PSScriptRoot 'TestSupport\Start-DgSpyHost.ps1') -RpcPort $RpcPort -TargetFramework $TargetFramework
    $gatewayProcess = Start-Process -FilePath 'dotnet' -ArgumentList ('"' + $gatewayDll + '"') `
        -WorkingDirectory (Split-Path $gatewayDll) -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $RunDirectory 'gateway.out') -RedirectStandardError (Join-Path $RunDirectory 'gateway.err')
    Initialize-McpClient -GatewayUrl $gatewayUrl -Token $gatewayToken
    $gatewayDeadline = [DateTime]::UtcNow.AddSeconds(40)
    while ([DateTime]::UtcNow -lt $gatewayDeadline) {
        try { $null = Invoke-RestMethod ($gatewayUrl + '/health') -TimeoutSec 1; break } catch { Start-Sleep -Milliseconds 250 }
    }
    $program = @(Rpc 'list_programs' @{ process_ids=@($targetId) })[0]
    $session = Rpc 'attach' @{ program_id=$program.program_id } 60
    $sessionId = $session.session_id
    Ensure-Paused $sessionId

    Say 'exact slot, preserve_stop'
    $result = Rpc 'run_atomic_action' (ActionArguments $sessionId $token 0 'preserve_stop') 60
    Check 'the action reached its slot and completed' ($result.action_outcome -eq 'completed') ("outcome=" + $result.action_outcome + " error=" + $result.error)
    Check 'nothing interrupted it' ($result.interruption_reason -eq 'none') ("interruption=" + $result.interruption_reason)
    Check 'cleanup completed' ($result.cleanup_outcome -eq 'completed') ("cleanup=" + $result.cleanup_outcome)
    Check 'the used slot is the requested one' ([int]$result.used_slot.il_offset -eq 0 -and [int]$result.used_slot.method_token -eq [int]$token) ("used=" + $result.used_slot.il_offset)
    Check 'the capture returned the argument value' ($result.verification_evidence -like '*41*') ("evidence=" + $result.verification_evidence)
    # ToUniversalTime, not a bare cast: PowerShell parses the round-trip string into local time and the
    # comparison against UtcNow then fails by the timezone offset rather than by the bound.
    Check 'the effective deadline is bounded' ($result.effective_deadline_utc -and ([DateTime]::Parse($result.effective_deadline_utc)).ToUniversalTime() -le ([DateTime]::UtcNow.AddSeconds(61))) ("deadline=" + $result.effective_deadline_utc)
    Check 'preserve_stop left the target paused' ($result.final_debugger_state.is_paused -eq $true) ("paused=" + $result.final_debugger_state.is_paused)
    $actionId = $result.audit_id
    Check 'an uninterrupted action names no reconciliation operation' (-not $result.reconciliation_operation) ("reconciliation=" + $result.reconciliation_operation)

    Say 'no stray owned breakpoint or stray pause survives the action'
    $breakpoints = @(Rpc 'list_breakpoints' @{})
    Check 'the action left no user-visible breakpoint behind' ($breakpoints.Count -eq 0) ("count=" + $breakpoints.Count)

    Say 'exact slot, resume'
    $resumeArguments = ActionArguments $sessionId $token 0 'resume'
    $resumeActionId = $resumeArguments.request.action_id
    $result = Rpc 'run_atomic_action' $resumeArguments 60
    Check 'the resume-policy action completed' ($result.action_outcome -eq 'completed') ("outcome=" + $result.action_outcome + " error=" + $result.error)
    Check 'resume cleanup completed rather than ambiguous' ($result.cleanup_outcome -eq 'completed') ("cleanup=" + $result.cleanup_outcome + " error=" + $result.error)
    Check 'resume actually left the target running' ($result.final_debugger_state.is_running -eq $true) ("running=" + $result.final_debugger_state.is_running)

    Say 'record operations'
    $status = Rpc 'get_atomic_action_status' @{ operation_version=1; action_id=$resumeActionId }
    Check 'the terminal record is retrievable by action_id' ($status.completed -eq $true -and $status.result -ne $null) ("completed=" + $status.completed)
    Check 'the retained record carries the outcome, not an empty completion' ($status.result.action_outcome -eq 'completed') ("outcome=" + $status.result.action_outcome)
    $reused = Rpc 'run_atomic_action' (ActionArguments $sessionId $token 0 'preserve_stop') 60
    Check 'a fresh action id is accepted while a terminal record is retained' ($reused.action_outcome -eq 'completed') ("outcome=" + $reused.action_outcome)
    $missing = $null
    try { $null = Rpc 'get_atomic_action_status' @{ operation_version=1; action_id='never-ran' } } catch { $missing = $_.Exception.Message }
    Check 'an unknown action id is refused rather than invented' ($missing -like '*action_not_found*') ("error=" + $missing)

    Say 'inconsistent process ids are refused'
    $mismatched = ActionArguments $sessionId $token 0 'preserve_stop'
    $mismatched.process_id = $targetId + 1000000
    $mismatchError = $null
    try { $null = Rpc 'run_atomic_action' $mismatched 60 } catch { $mismatchError = $_.Exception.Message }
    Check 'a mismatched process id pair is refused and both are named' ($mismatchError -like '*invalid_arguments*' -and $mismatchError -like "*$targetId*") ("error=" + $mismatchError)

    Say 'declared nearby slot'
    Ensure-Paused $sessionId
    # Offset 0 is declared as a nearby slot and 0x7fff0000 as the exact target, so the run can only
    # succeed by reporting the slot execution actually bound.
    $nearby = ActionArguments $sessionId $token 0 'preserve_stop' @(0)
    $nearby.request.il_offset = 60000
    $result = $null
    $nearbyError = $null
    try { $result = Rpc 'run_atomic_action' $nearby 60 } catch { $nearbyError = $_.Exception.Message }
    if ($result) {
        Check 'an unbindable exact target falls back to a declared nearby slot' ($result.action_outcome -eq 'completed' -or $result.action_outcome -eq 'nearby_slot_not_found') ("outcome=" + $result.action_outcome + " error=" + $result.error)
        if ($result.action_outcome -eq 'completed') {
            Check 'the reported slot is the one that bound, not the requested one' ([int]$result.used_slot.il_offset -eq 0) ("used=" + $result.used_slot.il_offset)
        }
        Check 'the nearby path still cleaned up' ($result.cleanup_outcome -ne 'failed' -and $result.cleanup_outcome -ne 'ambiguous') ("cleanup=" + $result.cleanup_outcome)
    }
    else {
        # An out-of-range offset the engine refuses outright is a legitimate answer; the action must
        # still refuse truthfully rather than hang or leave the target paused mid-cleanup.
        Check 'an out-of-range exact target is refused truthfully' ($nearbyError -ne $null) ("error=" + $nearbyError)
    }

    Say 'through the MCP surface, with the schema-declared guards'
    Ensure-Paused $sessionId
    # Invoke-MutatingTool reads tools/list, finds the required expected_*_version for this tool and
    # fills it from get_session_state, plus expected_stop_id because run_atomic_action is stop-bound.
    # Direct RPC cannot prove any of that: the extension only checks those guards when they are present.
    # A gateway session has no controller until one is claimed, and every mutating tool refuses until
    # then. That refusal is part of the MCP surface this leg exists to cross.
    $null = Invoke-Tool -Name 'claim_session' -Arguments @{ session_id=$sessionId }
    $mcpArguments = ActionArguments $sessionId $token 0 'preserve_stop'
    $mcpArguments.Remove('session_id') | Out-Null
    $mcpActionId = $mcpArguments.request.action_id
    $mcpResult = $null
    $mcpError = $null
    try { $mcpResult = Invoke-MutatingTool -Name 'run_atomic_action' -Arguments (@{ session_id=$sessionId } + $mcpArguments) } catch { $mcpError = $_.Exception.Message }
    Check 'the tool runs through the gateway with its declared version and stop guards' ($mcpResult -ne $null -and $mcpResult.action_outcome -eq 'completed') ("outcome=" + $mcpResult.action_outcome + " error=" + $mcpError)
    if ($mcpResult) {
        Check 'the gateway response carries a refreshed execution version' ($mcpResult.versions -ne $null -and $mcpResult.versions.execution_version -ne $null) ("versions=" + ($mcpResult.versions | ConvertTo-Json -Compress))
        Check 'the MCP-routed action is retrievable by action_id' ((Rpc 'get_atomic_action_status' @{ operation_version=1; action_id=$mcpActionId }).completed -eq $true) $mcpActionId
    }
    $staleError = $null
    try { $null = Invoke-Tool -Name 'run_atomic_action' -Arguments (@{ session_id=$sessionId; expected_execution_version=999999; expected_stop_id='not-a-stop' } + $mcpArguments) } catch { $staleError = $_.Exception.Message }
    Check 'a stale guard is refused rather than run' ($staleError -like '*stale*') ("error=" + $staleError)

    Say 'timeout under resume_policy=resume'
    Ensure-Paused $sessionId
    # Defect 1's live form: the lease used to release itself at the deadline, so the machine's own
    # cleanup-time resume threw through a lease that was gone and the target stayed paused.
    $timeoutArguments = ActionArguments $sessionId $neverCalledToken 0 'resume'
    $timeoutArguments.timeout_ms = 4000
    $timeoutActionId = $timeoutArguments.request.action_id
    $timeoutResult = Rpc 'run_atomic_action' $timeoutArguments 90
    Check 'a target that never reaches the slot times out' ($timeoutResult.interruption_reason -eq 'timeout') ("interruption=" + $timeoutResult.interruption_reason + " outcome=" + $timeoutResult.action_outcome + " error=" + $timeoutResult.error)
    Check 'a timeout still resumes the target' ($timeoutResult.final_debugger_state.is_running -eq $true) ("running=" + $timeoutResult.final_debugger_state.is_running)
    Check 'a timeout cleans up completely rather than ambiguously' ($timeoutResult.cleanup_outcome -eq 'completed') ("cleanup=" + $timeoutResult.cleanup_outcome + " error=" + $timeoutResult.error)
    Check 'a timed-out action left no user-visible breakpoint behind' (@(Rpc 'list_breakpoints' @{}).Count -eq 0) $timeoutActionId

    Say 'disconnect under complete_on_disconnect'
    Ensure-Paused $sessionId
    # Defect 7's live form: the client vanishes while the action runs, the policy says complete, so
    # the terminal record must report the timeout rather than client_disconnected.
    $disconnectArguments = ActionArguments $sessionId $neverCalledToken 0 'resume'
    $disconnectArguments.timeout_ms = 5000
    $disconnectActionId = $disconnectArguments.request.action_id
    Send-AndDisconnect 'run_atomic_action' $disconnectArguments
    $disconnected = Wait-ActionRecord $disconnectActionId
    Check 'a disconnected action still records a terminal result' ($disconnected.result -ne $null) ("completed=" + $disconnected.completed + " error=" + ($disconnected.error | ConvertTo-Json -Compress))
    if ($disconnected.result) {
        Check 'complete_on_disconnect reports the timeout, not the disconnect' ($disconnected.result.interruption_reason -eq 'timeout') ("interruption=" + $disconnected.result.interruption_reason)
        Check 'a disconnected action still resumed the target' ($disconnected.result.final_debugger_state.is_running -eq $true) ("running=" + $disconnected.result.final_debugger_state.is_running)
        Check 'a disconnected action still cleaned up' ($disconnected.result.cleanup_outcome -eq 'completed') ("cleanup=" + $disconnected.result.cleanup_outcome + " error=" + $disconnected.result.error)
    }

    Ensure-Paused $sessionId
    $null = Rpc 'detach' @{ session_id=$sessionId } 60
}
finally {
    try {
        $targetProcess = if ($targetId) { Get-Process -Id $targetId -ErrorAction SilentlyContinue } else { $null }
        if ($targetProcess) { $targetProcess.Kill(); $targetProcess.WaitForExit(10000) | Out-Null }
    } catch { }
    try {
        if ($targetLauncher -and -not $targetLauncher.HasExited) { $targetLauncher.Kill() }
        if ($targetLauncher) { $targetLauncher.WaitForExit(10000) | Out-Null }
    } catch { }
    try { if ($gatewayProcess -and -not $gatewayProcess.HasExited) { $gatewayProcess.Kill() } } catch { }
    if ($hostId) { Stop-Process -Id $hostId -Force -ErrorAction SilentlyContinue }
    Remove-Item Env:DGSPY_URL -ErrorAction SilentlyContinue
    Remove-Item Env:DGSPY_TOKEN -ErrorAction SilentlyContinue
    Remove-Item Env:DGSPY_OWNED_BP_TARGET_EXE -ErrorAction SilentlyContinue
    Remove-Item Env:DGSPY_OWNED_BP_TARGET_OUT -ErrorAction SilentlyContinue
    Remove-Item Env:DGSPY_OWNED_BP_TARGET_ERR -ErrorAction SilentlyContinue
    Say "passed=$pass failed=$fail"
    Say "artifacts=$RunDirectory"
}
if ($fail) { exit 1 }
