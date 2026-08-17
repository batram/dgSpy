#requires -Version 5.1
<#
.SYNOPSIS
	Dedicated live boundary for ordinary x64 CoreCLR debugging.

.DESCRIPTION
	Proves attach, pause, continue, breakpoint, frame evaluation, detach, and stale-target failure
	against a disposable net10.0 process. HookLab is deliberately absent from this gate.

	ASCII-only by policy. GUI-launching, so run it on a hidden desktop.
#>
[CmdletBinding()]
param(
	[int]$RpcPort = 0,
	[string]$RunDirectory
)

$ErrorActionPreference = 'Stop'
$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new()
$repoRoot = Split-Path $PSScriptRoot -Parent
$targetExe = Join-Path $repoRoot 'tests\TestTargets\CoreClrDebuggerTarget\bin\Release\net10.0\CoreClrDebuggerTarget.exe'
$logRoot = if ([string]::IsNullOrWhiteSpace($RunDirectory)) { Join-Path $PSScriptRoot ('artifacts\coreclr-debugger-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) } else { $RunDirectory }
$targetOut = Join-Path $logRoot 'target.out'
$targetErr = Join-Path $logRoot 'target.err'
$driverLog = Join-Path $logRoot 'driver.log'
$hostId = 0
$target = $null
$shortTarget = $null
$sessionId = $null
$pass = 0
$fail = 0

. (Join-Path $PSScriptRoot 'TestSupport\Invoke-DgSpyRpc.ps1')
. (Join-Path $PSScriptRoot 'TestSupport\Find-DgSpyProgram.ps1')

function Rpc([string]$Operation, [hashtable]$Arguments = @{}, [int]$Deadline = 30) {
	Invoke-DgSpyRpc -OperationName $Operation -OperationArguments $Arguments -RpcPort $RpcPort -DeadlineSeconds $Deadline
}
function Check([string]$Name, $Condition, [string]$Detail = '') {
	if ([bool]$Condition) { $script:pass++; Write-Host "PASS  $Name" -ForegroundColor DarkGreen }
	else { $script:fail++; Write-Host "FAIL  $Name  $Detail" -ForegroundColor Red }
}
function Wait-For([scriptblock]$Condition, [int]$Seconds = 20) {
	$deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
	do { Start-Sleep -Milliseconds 200; if (& $Condition) { return $true } } until ([DateTime]::UtcNow -gt $deadline)
	return $false
}
function Lines { @(Get-Content -LiteralPath $targetOut -ErrorAction SilentlyContinue) }

try {
	New-Item -ItemType Directory -Force -Path $logRoot | Out-Null
	Start-Transcript -LiteralPath $driverLog -Force | Out-Null
	if (-not (Test-Path -LiteralPath $targetExe)) { throw "CoreCLR fixture is missing: $targetExe" }
	$target = Start-Process -FilePath $targetExe -WindowStyle Hidden -PassThru -RedirectStandardOutput $targetOut -RedirectStandardError $targetErr
	if (-not (Wait-For { @(Lines | Where-Object { $_ -like 'READY *' }).Count -eq 1 })) { throw 'CoreCLR fixture did not become ready.' }

	$hostId = & (Join-Path $PSScriptRoot 'TestSupport\Start-DgSpyHost.ps1') -RpcPort $RpcPort
	$RpcPort = [int]$env:DGSPY_RPC_PORT
	$program = Find-DgSpyProgram -ProcessId $target.Id -InvokeRpc ${function:Rpc}
	$attached = Rpc 'attach' @{ program_id=$program.program_id } 60
	$sessionId = $attached.session_id
	Check 'attach returns a live CoreCLR session' ($attached.state -in @('running','paused') -and $sessionId) ("state=" + $attached.state)

	if ($attached.state -eq 'paused') { $null = Rpc 'continue' @{ session_id=$sessionId } }
	$before = @(Lines).Count
	Check 'continue permits target progress' (Wait-For { @(Lines).Count -gt $before } 10)

	$paused = Rpc 'pause' @{ session_id=$sessionId }
	Check 'pause reports an intentional stop' ($paused.state -eq 'paused' -and $paused.stop_id) ("state=" + $paused.state)
	$resumed = Rpc 'continue' @{ session_id=$sessionId }
	Check 'continue resumes an intentional stop' ($resumed.state -in @('running','paused')) ("state=" + $resumed.state)

	if ($resumed.state -eq 'running') { $null = Rpc 'pause' @{ session_id=$sessionId } }
	$breakpoint = Rpc 'set_breakpoint' @{ session_id=$sessionId; module='CoreClrDebuggerTarget'; type='CoreClrDebuggerTarget.Program'; method='Probe' }
	Check 'a named CoreCLR breakpoint binds' ($breakpoint.bound -and $breakpoint.breakpoint_id) ("message=" + $breakpoint.message)
	$cursor = $breakpoint.cursor_event_id
	$null = Rpc 'continue' @{ session_id=$sessionId }
	$stop = Rpc 'wait_for_stop' @{ session_id=$sessionId; after_event_id=$cursor; timeout_ms=15000 } 20
	$breakState = Rpc 'get_session_state' @{ session_id=$sessionId }
	Check 'execution reaches the CoreCLR breakpoint' (-not $stop.timed_out -and $breakState.state -eq 'paused') ("state=" + $breakState.state)
	$frames = @(Rpc 'get_callstack' @{ session_id=$sessionId; max_frames=20 })
	$frame = @($frames | Where-Object { $_.name -like '*Program.Probe*' } | Select-Object -First 1)[0]
	Check 'the breakpoint stop exposes its target frame' ($null -ne $frame)
	if ($frame) {
		$value = Rpc 'evaluate' @{ session_id=$sessionId; expression='value'; thread_id=$frame.thread_id; frame_index=$frame.frame_index }
		Check 'evaluation reads a CoreCLR frame local' ($null -eq $value.error -and [int]$value.value -ge 0) ("value=" + $value.value + " error=" + $value.error)
	}

	$state = Rpc 'get_session_state' @{ session_id=$sessionId }
	$detached = Rpc 'detach' @{ session_id=$sessionId }
	$sessionId = $null
	Check 'detach is non-terminating' ($detached.detached -and -not $detached.terminated)
	Check 'the CoreCLR target survives detach' (-not $target.HasExited)

	$shortOut = Join-Path $logRoot 'short.out'
	$shortTarget = Start-Process -FilePath $targetExe -WindowStyle Hidden -PassThru -RedirectStandardOutput $shortOut -RedirectStandardError (Join-Path $logRoot 'short.err')
	$shortProgram = Find-DgSpyProgram -ProcessId $shortTarget.Id -InvokeRpc ${function:Rpc}
	Stop-Process -Id $shortTarget.Id -Force
	$shortTarget.WaitForExit(5000) | Out-Null
	$failure = $null
	$failedAttach = $null
	try { $failedAttach = Rpc 'attach' @{ program_id=$shortProgram.program_id } 20 } catch { $failure = $_.Exception.Message }
	$failedDurably = $failure -or ($failedAttach -and $failedAttach.state -in @('terminated','faulted'))
	Check 'attach to an exited CoreCLR target fails durably' $failedDurably ("error=" + $failure + " result=" + ($failedAttach | ConvertTo-Json -Compress -Depth 5))
}
finally {
	if ($sessionId) { try { $null = Rpc 'detach' @{ session_id=$sessionId } 20 } catch {} }
	foreach ($processId in @($hostId, $(if ($target) { $target.Id } else { 0 }), $(if ($shortTarget) { $shortTarget.Id } else { 0 }))) {
		if ($processId -gt 0) { Stop-Process -Id $processId -Force -ErrorAction SilentlyContinue }
	}
	Write-Host "CoreCLR debugger smoke: $pass passed, $fail failed" -ForegroundColor Cyan
	Write-Host "logs: $logRoot" -ForegroundColor DarkGray
	try { Stop-Transcript | Out-Null } catch {}
}
if ($fail -ne 0) { exit 1 }
