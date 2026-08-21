#requires -Version 5.1
<#
.SYNOPSIS
	Cycles a compiled hook through install, update and remove against a live Mono target, and reports
	where it stops. ASCII-only; run on a hidden desktop.

.DESCRIPTION
	A diagnostic instrument, not a gate. run-mono-hooklab-smoke.ps1 drives the lifecycle once and says
	pass or fail; this drives the interesting part of it dozens of times and prints a timing per step, so
	an intermittent has somewhere to show up.

	That difference is why it exists. The control-channel stall documented in HOOKLAB.md appeared in
	roughly one gate run in six - too rare to study, and easy to dismiss as a flake. The same fault
	appears here every two to seven cycles, because each cycle is three round trips that make the target
	compile and patch while its own loop is calling the patched method. It took this loop to establish
	that the stall is not Unity-specific, is not specific to removal, and freezes the whole VM rather
	than just the resident's command thread.

	On a failure it does not simply exit. It reports whether the target is still alive, whether the
	target's own unrelated heartbeat thread is still advancing, and what the debugger thinks the session
	state is - which is the three-way distinction between a wedged resident, a suspended VM, and a dead
	target. Getting that wrong sends an investigation in entirely the wrong direction.

	No Mono is shipped or discovered: pass -MonoExe or set DGSPY_MONO_EXE. The -MonoRuntime and
	-MonoAssemblies parameters take the Mono a Unity player embeds, exactly as the gate and the
	compatibility probe do.

.EXAMPLE
	$env:DGSPY_MONO_EXE = 'C:\Program Files\Mono\bin\mono.exe'
	.\tests\run-mono-hooklab-stress.ps1 -Cycles 40
#>
[CmdletBinding()]
param(
	[int]$Cycles = 20,
	[string]$MonoExe = $env:DGSPY_MONO_EXE,
	[string]$MonoRuntime = $env:DGSPY_MONO_RUNTIME,
	[string]$MonoAssemblies = $env:DGSPY_MONO_ASSEMBLIES,
	# Both names dnSpy gives the one soft-debugger engine. Tested: which one is used does not change the
	# stall, so this exists to keep that answerable rather than to be tuned.
	[ValidateSet('mono','unity')][string]$Engine = 'mono',
	[int]$AgentPort = 0,
	[int]$RpcPort = 0,
	[ValidateSet('net10.0-windows','net48')][string]$TargetFramework = 'net10.0-windows',
	[string]$RunDirectory
)

$ErrorActionPreference = 'Stop'
$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new()
$repoRoot = Split-Path $PSScriptRoot -Parent
$targetExe = Join-Path $repoRoot 'tests\TestTargets\MonoHookLabTarget\bin\Release\net48\MonoHookLabTarget.exe'
$root = if ($RunDirectory) { $RunDirectory } else { Join-Path $PSScriptRoot ('artifacts\mono-stress-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
$hostId = 0; $target = $null; $sessionId = $null; $failures = 0; $completed = 0

. (Join-Path $PSScriptRoot 'TestSupport\Invoke-DgSpyRpc.ps1')
function Rpc([string]$Operation,[hashtable]$Arguments=@{},[int]$Deadline=60) { Invoke-DgSpyRpc -OperationName $Operation -OperationArguments $Arguments -RpcPort $RpcPort -DeadlineSeconds $Deadline }
function Behavior { $v=(Get-Content -LiteralPath (Join-Path $root 'behavior.txt') -ErrorAction SilentlyContinue | Select-Object -First 1); if($v){[int]$v}else{-1} }
function Heartbeat { $v=(Get-Content -LiteralPath (Join-Path $root 'heartbeat.txt') -ErrorAction SilentlyContinue | Select-Object -First 1); if($v){[long]$v}else{-1} }
function WaitFor([int]$Value,[int]$Seconds=20) { $d=[DateTime]::UtcNow.AddSeconds($Seconds); do { if((Behavior) -eq $Value){return $true}; Start-Sleep -Milliseconds 100 } until([DateTime]::UtcNow -gt $d); return $false }

# The three-way distinction. A resident whose command thread is stuck leaves the rest of the process
# running; a suspended VM freezes even threads that have nothing to do with dgSpy; a dead target answers
# nothing at all. They look identical from the failed call alone.
function Report-Stall([string]$Operation) {
	$h1 = Heartbeat; $b1 = Behavior
	Start-Sleep -Seconds 3
	$h2 = Heartbeat; $b2 = Behavior
	$alive = ($target -ne $null) -and (-not $target.HasExited)
	Write-Host ("    target_alive=$alive  heartbeat $h1->$h2  behaviour $b1->$b2") -ForegroundColor Yellow
	if (-not $alive) { Write-Host '    the target died under this operation' -ForegroundColor Yellow; return }
	if ($h1 -eq $h2) { Write-Host '    the whole VM is frozen, not just the resident: expect state=paused below' -ForegroundColor Yellow }
	else { Write-Host '    the target is still running, so the resident itself is stuck' -ForegroundColor Yellow }
	try { Write-Host ("    session state=" + (Rpc 'get_session_state' @{ session_id=$sessionId } 20).state) -ForegroundColor Yellow }
	catch { Write-Host ("    session state unavailable: " + $_.Exception.Message) -ForegroundColor Yellow }
}

function Get-MethodFacts([string]$AssemblyPath,[string]$TypeName,[string]$MethodName) {
	$assembly = [Reflection.Assembly]::LoadFrom($AssemblyPath)
	$method = $assembly.GetType($TypeName,$true).GetMethod($MethodName,[Reflection.BindingFlags]'Static,Public')
	$il = $method.GetMethodBody().GetILAsByteArray()
	$sha = [Security.Cryptography.SHA256]::Create()
	try { $digest = [BitConverter]::ToString($sha.ComputeHash($il)).Replace('-','').ToLowerInvariant() } finally { $sha.Dispose() }
	[pscustomobject]@{ Token=[uint32]$method.MetadataToken; Mvid=$method.Module.ModuleVersionId.ToString('D')
		Signature=$method.ReturnType.FullName+' '+$MethodName+'('+(($method.GetParameters()|ForEach-Object{$_.ParameterType.FullName}) -join ',')+')'
		IlSha256=$digest }
}

try {
	if ([string]::IsNullOrWhiteSpace($MonoExe)) { throw 'Pass -MonoExe or set DGSPY_MONO_EXE; dgSpy does not ship or discover a Mono.' }
	if (-not (Test-Path -LiteralPath $MonoExe)) { throw "No Mono runtime at $MonoExe." }
	if (-not (Test-Path -LiteralPath $targetExe)) { throw "Fixture not built: $targetExe. dotnet build tests\TestTargets\MonoHookLabTarget\MonoHookLabTarget.csproj -c Release" }
	New-Item -ItemType Directory -Force -Path $root | Out-Null
	Start-Transcript -LiteralPath (Join-Path $root 'stress.log') -Force | Out-Null

	if ($AgentPort -eq 0) { $p=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0); $p.Start(); $AgentPort=$p.LocalEndpoint.Port; $p.Stop() }
	$facts = Get-MethodFacts $targetExe 'MonoHookLabTarget.Program' 'Work'
	$agent = "--debugger-agent=transport=dt_socket,server=y,address=127.0.0.1:$AgentPort,suspend=n"
	$arguments = if ($MonoRuntime) { @(('"'+$MonoRuntime+'"'),$agent,('"'+$targetExe+'"'),('"'+$root+'"')) } else { @($agent,('"'+$targetExe+'"'),('"'+$root+'"')) }
	if ($MonoAssemblies) { $env:MONO_PATH = $MonoAssemblies }
	$target = Start-Process -FilePath $MonoExe -ArgumentList $arguments -WindowStyle Hidden -PassThru `
		-RedirectStandardOutput (Join-Path $root 'target.out') -RedirectStandardError (Join-Path $root 'target.err')
	# Caching the handle keeps ExitCode readable later; see the same note in run-mono-hooklab-smoke.ps1.
	$null = $target.Handle
	$deadline = [DateTime]::UtcNow.AddSeconds(30)
	while (-not (Test-Path (Join-Path $root 'ready.txt')) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 200 }
	if (-not (Test-Path (Join-Path $root 'ready.txt'))) { throw 'The fixture did not report ready within 30 seconds.' }

	$hostId = & (Join-Path $PSScriptRoot 'TestSupport\Start-DgSpyHost.ps1') -RpcPort $RpcPort -TargetFramework $TargetFramework
	$RpcPort = [int]$env:DGSPY_RPC_PORT
	$attached = Rpc 'attach_endpoint' @{ address='127.0.0.1'; port=$AgentPort; engine=$Engine; process_is_suspended=$false; connection_timeout_ms=30000 }
	$sessionId = $attached.session_id
	$processId = @($attached.process_ids)[0]
	$modules = @((Rpc 'list_modules' @{ session_id=$sessionId; name_pattern='MonoHookLabTarget' }).modules)
	# Arrival needs a suspended thread with managed frames, so stop somewhere real first.
	$bp = Rpc 'set_breakpoint' @{ session_id=$sessionId; module=$modules[0].name; module_id=$modules[0].module_id; type='MonoHookLabTarget.Program'; method='Tick' }
	$null = Rpc 'wait_for_stop' @{ session_id=$sessionId; after_event_id=$bp.cursor_event_id; timeout_ms=30000 }
	$null = Rpc 'clear_breakpoints' @{ session_id=$sessionId }
	$null = Rpc 'initialize_hooklab' @{ session_id=$sessionId; process_id=$processId } 120
	if (-not (Rpc 'get_session_state' @{ session_id=$sessionId }).is_running) { $null = Rpc 'continue' @{ session_id=$sessionId } }
	Write-Host "resident is up on $($Engine); cycling $Cycles times" -ForegroundColor Cyan

	$hook = @{ session_id=$sessionId; process_id=$processId; hook_id='stress'; kind='Postfix'
		module_id=$modules[0].module_id; assembly='MonoHookLabTarget'; declaring_type='MonoHookLabTarget.Program'; method='Work'
		method_token=[int]$facts.Token; signature=$facts.Signature; module_mvid=$facts.Mvid; il_sha256=$facts.IlSha256 }

	for ($cycle = 1; $cycle -le $Cycles; $cycle++) {
		$watch = [Diagnostics.Stopwatch]::StartNew()
		$hook.revision = 1; $hook.source = 'public static class StressV1 { public static void Postfix(ref int __result) { __result = 777; } }'
		try { $null = Rpc 'create_hook' $hook 60 } catch { $failures++; Write-Host ("cycle $cycle  create FAILED after $($watch.ElapsedMilliseconds) ms: " + $_.Exception.Message) -ForegroundColor Red; Report-Stall 'create_hook'; break }
		$created = $watch.ElapsedMilliseconds
		# The wait matters: it is what makes the hook actually be called when the next operation lands on
		# it. Without it the loop runs clean for 30 cycles and proves nothing.
		$sawV1 = WaitFor 777
		$hook.revision = 2; $hook.source = 'public static class StressV2 { public static void Postfix(ref int __result) { __result = 555; } }'
		$watch.Restart()
		try { $null = Rpc 'update_hook' $hook 60 } catch { $failures++; Write-Host ("cycle $cycle  update FAILED after $($watch.ElapsedMilliseconds) ms: " + $_.Exception.Message) -ForegroundColor Red; Report-Stall 'update_hook'; break }
		$updated = $watch.ElapsedMilliseconds
		$sawV2 = WaitFor 555
		$watch.Restart()
		try { $null = Rpc 'remove_hook' @{ session_id=$sessionId; process_id=$processId; hook_id='stress' } 60 } catch { $failures++; Write-Host ("cycle $cycle  remove FAILED after $($watch.ElapsedMilliseconds) ms: " + $_.Exception.Message) -ForegroundColor Red; Report-Stall 'remove_hook'; break }
		$restored = WaitFor 42
		$completed++
		Write-Host ("cycle {0,3}  create {1,6} ms  update {2,6} ms  remove {3,6} ms  behaviour v1={4} v2={5} restored={6}" -f $cycle,$created,$updated,$watch.ElapsedMilliseconds,$sawV1,$sawV2,$restored)
	}
}
catch {
	$failures++
	Write-Host ("the stress run did not finish: " + $_.Exception.Message) -ForegroundColor Red
	Write-Host $_.ScriptStackTrace
}
finally {
	if ($sessionId) { try { $null = Rpc 'detach' @{ session_id=$sessionId } 30 } catch { } }
	if ($target -and -not $target.HasExited) { Stop-Process -Id $target.Id -Force -ErrorAction SilentlyContinue }
	if ($hostId -gt 0) { Stop-Process -Id $hostId -Force -ErrorAction SilentlyContinue }
	Write-Host "Mono HookLab stress: $completed cycles completed, $failures failure(s)" -ForegroundColor Cyan
	Write-Host "logs: $root"
	try { Stop-Transcript | Out-Null } catch { }
}
if ($failures -ne 0) { exit 1 }
exit 0
