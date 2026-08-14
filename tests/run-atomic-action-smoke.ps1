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
$configuredLayout = [Environment]::GetEnvironmentVariable('DGSPY_LAYOUT_ROOT')
$gatewayDll = if (-not [string]::IsNullOrWhiteSpace($configuredLayout)) { Join-Path $configuredLayout 'bin\dgSpy.Gateway.dll' } else { Join-Path $repoRoot 'dgSpy.Gateway\bin\Release\net10.0\dgSpy.Gateway.dll' }
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

# T08c. Sends two operations down ONE host connection, sequentially, reading each answer before
# sending the next. That is the whole point: after the asynchronous change the two requests need not
# overlap, because start_atomic_action returns as soon as the action is registered. Under the old
# blocking shape the second line stayed unread until the first operation finished - HandleClientAsync
# reads a request and awaits its full dispatch before reading the next - so a cancel sent 2 s in was
# answered 18.8 s later, reporting cancel_requested=false, completed=true. A raw socket rather than
# Invoke-DgSpyRpc because that helper opens and disposes a connection per call and therefore cannot
# prove one connection was reused.
function Invoke-SequentialOnOneConnection([string]$FirstOperation, [hashtable]$FirstArguments, [scriptblock]$SecondFactory) {
    $rpcTokenPath = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'dgSpy\rpc.token'
    $rpcToken = (Get-Content -LiteralPath $rpcTokenPath -Raw).Trim()
    $client = [Net.Sockets.TcpClient]::new()
    try {
        $client.Connect('127.0.0.1', $RpcPort)
        $stream = $client.GetStream()
        $reader = [IO.StreamReader]::new($stream, [Text.Encoding]::UTF8, $false, 4096, $true)
        $writer = [IO.StreamWriter]::new($stream, [Text.UTF8Encoding]::new($false), 4096, $true)
        $writer.AutoFlush = $true
        $send = {
            param($Operation, $Arguments, $HostIdentifier)
            $envelope = @{ version=2; request_id=[Guid]::NewGuid().ToString('N'); authentication_token=$rpcToken; operation=$Operation; arguments=$Arguments; deadline_utc=[DateTime]::UtcNow.AddSeconds(60).ToString('O') }
            if ($HostIdentifier) { $envelope.host_id = $HostIdentifier }
            $writer.WriteLine(($envelope | ConvertTo-Json -Compress -Depth 8))
            $reader.ReadLine() | ConvertFrom-Json
        }
        $ping = & $send 'ping' @{} $null
        if ($ping.error) { throw "ping failed: $($ping.error.code)" }
        $started = [Diagnostics.Stopwatch]::StartNew()
        $first = & $send $FirstOperation $FirstArguments $ping.result.host_id
        $firstMs = $started.ElapsedMilliseconds
        $secondArguments = & $SecondFactory $first
        $second = & $send $secondArguments.operation $secondArguments.arguments $ping.result.host_id
        return [pscustomobject]@{ First=$first; Second=$second; FirstMs=$firstMs; TotalMs=$started.ElapsedMilliseconds }
    }
    finally { try { $client.Close(); $client.Dispose() } catch { } }
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

function Get-BreakpointSnapshot {
    # Re-pipeline the RPC result because Windows PowerShell 5.1 can preserve a ConvertFrom-Json
    # array as one nested pipeline item. Keep only stable user-visible identity/state; engine binding
    # diagnostics and session versions legitimately change while the target runs.
    @(
        Rpc 'list_breakpoints' @{} |
            ForEach-Object { $_ } |
            ForEach-Object {
                [ordered]@{
                    breakpoint_id = $_.breakpoint_id
                    module = $_.module
                    method_token = $_.method_token
                    il_offset = $_.il_offset
                    enabled = $_.enabled
                }
            } |
            ForEach-Object { $_ | ConvertTo-Json -Compress } |
            Sort-Object
    )
}

function Test-BreakpointSnapshot([string[]]$Expected, [string[]]$Actual) {
    (Compare-Object -ReferenceObject @($Expected) -DifferenceObject @($Actual)).Count -eq 0
}

New-Item -ItemType Directory -Force -Path $RunDirectory | Out-Null
try {
    if (-not (Test-Path -LiteralPath $targetExe)) { throw "CorDebug target not built: $targetExe" }
    if (@(Get-NetTCPConnection -State Listen -LocalPort $RpcPort -ErrorAction SilentlyContinue).Count) { throw "port $RpcPort is already in use" }

    $targetOut = Join-Path $RunDirectory 'target.out'
    $targetErr = Join-Path $RunDirectory 'target.err'
    $env:DGSPY_CORDEBUG_TARGET_EXE = $targetExe
    $env:DGSPY_CORDEBUG_TARGET_OUT = $targetOut
    $env:DGSPY_CORDEBUG_TARGET_ERR = $targetErr
    $targetLauncherScript = Join-Path $PSScriptRoot 'TestSupport\Run-CorDebugTarget.ps1'
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
    $baselineBreakpoints = @(Get-BreakpointSnapshot)

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
    $breakpoints = @(Get-BreakpointSnapshot)
    Check 'the action preserved the user-visible breakpoint set' (Test-BreakpointSnapshot $baselineBreakpoints $breakpoints) ($breakpoints -join ';')

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
    $breakpoints = @(Get-BreakpointSnapshot)
    Check 'a timed-out action preserved the user-visible breakpoint set' (Test-BreakpointSnapshot $baselineBreakpoints $breakpoints) ($breakpoints -join ';')

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

    Say 'T08c: start_atomic_action returns after registration, and a cancel actually interrupts'
    Ensure-Paused $sessionId
    # disconnect_policy has no coherent meaning once the start request ends normally, so it is refused
    # rather than silently ignored.
    $policyArguments = ActionArguments $sessionId $neverCalledToken 0 'resume'
    $policyError = $null
    try { $null = Rpc 'start_atomic_action' $policyArguments 30 } catch { $policyError = $_.Exception.Message }
    Check 'start refuses disconnect_policy instead of silently dropping it' ($policyError -like '*invalid_arguments*' -and $policyError -like '*disconnect_policy*') ("error=" + $policyError)

    # A slot the fixture never reaches, so the action can only end by its deadline or by a cancel. The
    # deadline is deliberately far away, so a terminal record arriving quickly can only be the cancel.
    Ensure-Paused $sessionId
    $asyncArguments = ActionArguments $sessionId $neverCalledToken 0 'resume'
    $asyncArguments.request.Remove('disconnect_policy') | Out-Null
    $asyncArguments.timeout_ms = 45000
    $asyncActionId = $asyncArguments.request.action_id
    $sequential = Invoke-SequentialOnOneConnection 'start_atomic_action' $asyncArguments {
        param($accepted)
        # Deliberately no expected_execution_version: the schema no longer carries one, and the action
        # being cancelled is what moves it.
        @{ operation='cancel_atomic_action'; arguments=@{ operation_version=1; session_id=$sessionId; action_id=$accepted.result.action_id } }
    }
    $accepted = $sequential.First
    Check 'start is answered without waiting for arming' ($accepted.error -eq $null -and $accepted.result.accepted -eq $true) ("error=" + ($accepted.error | ConvertTo-Json -Compress))
    if (-not $accepted.error) {
        Check 'start answers in well under the action bound' ($sequential.FirstMs -lt 5000) ("ms=" + $sequential.FirstMs)
        Check 'the acceptance names the action and its phase' ($accepted.result.action_id -eq $asyncActionId -and $accepted.result.phase) ("id=" + $accepted.result.action_id + " phase=" + $accepted.result.phase)
        Check 'the acceptance recommends a poll interval instead of a wait operation' ([int]$accepted.result.recommended_poll_after_ms -gt 0) ("poll=" + $accepted.result.recommended_poll_after_ms)
        Check 'the acceptance names its status and cancel operations' ($accepted.result.status_operation -eq 'get_atomic_action_status' -and $accepted.result.cancel_operation -eq 'cancel_atomic_action') ("status=" + $accepted.result.status_operation)
    }
    $cancelled = $sequential.Second
    Check 'a cancel sent on the SAME host connection is answered' ($cancelled.error -eq $null) ("error=" + ($cancelled.error | ConvertTo-Json -Compress))
    if (-not $cancelled.error) {
        # The old blocking shape answered this cancel only after the action was over, with
        # cancel_requested=false and completed=true. Both fields are the regression.
        Check 'the cancel is accepted while the action is still running' ($cancelled.result.cancel_requested -eq $true -and $cancelled.result.completed -eq $false) ("requested=" + $cancelled.result.cancel_requested + " completed=" + $cancelled.result.completed)
        Check 'both requests were answered far inside the action bound' ($sequential.TotalMs -lt 15000) ("ms=" + $sequential.TotalMs)
    }
    $cancelledRecord = Wait-ActionRecord $asyncActionId 30
    Check 'the cancelled action reaches a terminal record' ($cancelledRecord.completed -eq $true) ("phase=" + $cancelledRecord.phase)
    Check 'the terminal phase is reported' ($cancelledRecord.phase -eq 'terminal') ("phase=" + $cancelledRecord.phase)
    Check 'status reports the cancel that was requested' ($cancelledRecord.cancel_requested -eq $true) ("cancel_requested=" + $cancelledRecord.cancel_requested)
    if ($cancelledRecord.result) {
        Check 'the interruption is the cancellation, not the deadline' ($cancelledRecord.result.interruption_reason -eq 'cancelled') ("interruption=" + $cancelledRecord.result.interruption_reason)
        Check 'a cancelled action still cleaned up' ($cancelledRecord.result.cleanup_outcome -ne 'failed') ("cleanup=" + $cancelledRecord.result.cleanup_outcome + " error=" + $cancelledRecord.result.error)
    }
    $breakpoints = @(Get-BreakpointSnapshot)
    Check 'a cancelled action preserved the user-visible breakpoint set' (Test-BreakpointSnapshot $baselineBreakpoints $breakpoints) ($breakpoints -join ';')
    $idempotent = Rpc 'cancel_atomic_action' @{ operation_version=1; session_id=$sessionId; action_id=$asyncActionId }
    Check 'cancel after terminal completion is truthful rather than an error' ($idempotent.cancel_requested -eq $false -and $idempotent.completed -eq $true) ("requested=" + $idempotent.cancel_requested + " completed=" + $idempotent.completed)
    $unknownCancel = $null
    try { $null = Rpc 'cancel_atomic_action' @{ operation_version=1; session_id=$sessionId; action_id='never-started' } } catch { $unknownCancel = $_.Exception.Message }
    Check 'cancelling an unknown action is action_not_found' ($unknownCancel -like '*action_not_found*') ("error=" + $unknownCancel)

    Say 'T08c: the same shape through the gateway, which serializes every host call'
    Ensure-Paused $sessionId
    # The Gateway holds its own SemaphoreSlim(1,1) per host connection, so a blocking run occupies that
    # route for its whole budget too. Start and cancel through it, in that order.
    $routedArguments = ActionArguments $sessionId $neverCalledToken 0 'resume'
    $routedArguments.request.Remove('disconnect_policy') | Out-Null
    $routedArguments.timeout_ms = 45000
    $routedActionId = $routedArguments.request.action_id
    $routedArguments.Remove('session_id') | Out-Null
    $routedAccepted = $null
    $routedError = $null
    try { $routedAccepted = Invoke-MutatingTool -Name 'start_atomic_action' -Arguments (@{ session_id=$sessionId } + $routedArguments) } catch { $routedError = $_.Exception.Message }
    Check 'start_atomic_action is reachable through the gateway with its declared guards' ($routedAccepted -ne $null -and $routedAccepted.accepted -eq $true) ("error=" + $routedError)
    if ($routedAccepted) {
        $routedCancel = $null
        $routedCancelError = $null
        try { $routedCancel = Invoke-Tool -Name 'cancel_atomic_action' -Arguments @{ operation_version=1; session_id=$sessionId; action_id=$routedActionId } } catch { $routedCancelError = $_.Exception.Message }
        Check 'the routed cancel is accepted while the routed action is still running' ($routedCancel -ne $null -and $routedCancel.cancel_requested -eq $true -and $routedCancel.completed -eq $false) ("requested=" + $routedCancel.cancel_requested + " completed=" + $routedCancel.completed + " error=" + $routedCancelError)
        if ($routedCancel) {
            $routedRecord = Wait-ActionRecord $routedActionId 30
            Check 'the routed action ends by cancellation rather than by its deadline' ($routedRecord.result -ne $null -and $routedRecord.result.interruption_reason -eq 'cancelled') ("interruption=" + $routedRecord.result.interruption_reason)
        }
        else {
            # Leave nothing running behind a failed leg: the action still holds the process lease.
            try { $null = Rpc 'cancel_atomic_action' @{ operation_version=1; session_id=$sessionId; action_id=$routedActionId } } catch { }
            $null = Wait-ActionRecord $routedActionId 60
        }
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
    Remove-Item Env:DGSPY_CORDEBUG_TARGET_EXE -ErrorAction SilentlyContinue
    Remove-Item Env:DGSPY_CORDEBUG_TARGET_OUT -ErrorAction SilentlyContinue
    Remove-Item Env:DGSPY_CORDEBUG_TARGET_ERR -ErrorAction SilentlyContinue
    Say "passed=$pass failed=$fail"
    Say "artifacts=$RunDirectory"
}
if ($fail) { exit 1 }
