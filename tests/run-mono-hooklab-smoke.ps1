#requires -Version 5.1
<#
.SYNOPSIS
	Live HookLab lifecycle on a plain Mono target, over the soft debugger. ASCII-only; hidden desktop.

.DESCRIPTION
	The gate for Mono arrival, and deliberately not a Unity one. Mono is the runtime; a Unity player
	merely embeds it, and the arrival path is identical - one debugger evaluation, placed on an owned
	internal breakpoint the Mono engine now provides. Proving it on a target this repository can build
	and launch, under a Mono the caller supplies, is what makes "HookLab works on Mono" a statement
	about the runtime rather than about one game engine's player.

	run-unity-hooklab-smoke.ps1 is the special case of this: same tools, same order, against a player
	that needs a Unity editor and a licence to exist.

	The compatibility probe's mono leg covers everything downstream of arrival in seconds, but it loads
	the payload into its own fixture - the delivery path minus the debugger. This is the part the probe
	structurally cannot prove.

	No Mono is shipped or discovered: which Mono happens to be installed must not decide what was
	measured. Pass -MonoExe or set DGSPY_MONO_EXE, and it must be x64 - the resident's architecture
	guard refuses an x86 target, so a 32-bit Mono is rejected rather than quietly measured.

.EXAMPLE
	$env:DGSPY_MONO_EXE = 'C:\Program Files\Mono\bin\mono.exe'
	.\tests\run-mono-hooklab-smoke.ps1
#>
[CmdletBinding()]
param(
	[string]$MonoExe = $env:DGSPY_MONO_EXE,
	# The Mono a Unity player embeds, which cannot be run the simple way: Unity ships mono.exe as x86
	# only, so an x64 target needs MonoHost64 to load the player's x64 runtime and call mono_main, and
	# MONO_PATH must point at the editor's unityjit-win32 profile - byte-identical to a shipped player's
	# Managed directory. Same parameters as the compatibility probe's --mono-runtime/--mono-assemblies,
	# so one runtime is described one way across both.
	[string]$MonoRuntime = $env:DGSPY_MONO_RUNTIME,
	[string]$MonoAssemblies = $env:DGSPY_MONO_ASSEMBLIES,
	# 0 takes a free ephemeral port. Windows reserves large TCP ranges for Hyper-V/WinNAT and the
	# ranges move between boots, so no fixed port is safe.
	[int]$AgentPort = 0,
	[int]$RpcPort = 0,
	[ValidateSet('net10.0-windows','net48')][string]$TargetFramework = 'net10.0-windows',
	[string]$RunDirectory
)

$ErrorActionPreference = 'Stop'
$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new()
$repoRoot = Split-Path $PSScriptRoot -Parent
$targetExe = Join-Path $repoRoot 'tests\TestTargets\MonoHookLabTarget\bin\Release\net48\MonoHookLabTarget.exe'
$root = if ($RunDirectory) { $RunDirectory } else { Join-Path $PSScriptRoot ('artifacts\mono-hooklab-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
$hostId = 0; $target = $null; $sessionId = $null; $pass = 0; $fail = 0

. (Join-Path $PSScriptRoot 'TestSupport\Invoke-DgSpyRpc.ps1')
function Rpc([string]$Operation,[hashtable]$Arguments=@{},[int]$Deadline=70) { Invoke-DgSpyRpc -OperationName $Operation -OperationArguments $Arguments -RpcPort $RpcPort -DeadlineSeconds $Deadline }
function Check([string]$Name,$Condition,[string]$Detail='') { if([bool]$Condition){$script:pass++;Write-Host "PASS  $Name" -ForegroundColor DarkGreen}else{$script:fail++;Write-Host "FAIL  $Name  $Detail" -ForegroundColor Red} }
function Wait-Until([scriptblock]$Condition,[int]$Seconds=20) { $deadline=[DateTime]::UtcNow.AddSeconds($Seconds); do { if(& $Condition){return $true}; Start-Sleep -Milliseconds 200 } until([DateTime]::UtcNow -gt $deadline); return $false }
function Behavior { $value=(Get-Content -LiteralPath (Join-Path $root 'behavior.txt') -ErrorAction SilentlyContinue | Select-Object -First 1); if($value){[int]$value}else{-1} }

# Reflection over the file on disk, not the live target: the guards create_hook checks are properties of
# the assembly, and deriving them from the debugger would be asking the thing under test to describe
# itself.
function Get-MethodFacts([string]$AssemblyPath,[string]$TypeName,[string]$MethodName) {
	$assembly = [Reflection.Assembly]::LoadFrom($AssemblyPath)
	$method = $assembly.GetType($TypeName,$true).GetMethod($MethodName,[Reflection.BindingFlags]'Static,Public')
	$il = $method.GetMethodBody().GetILAsByteArray()
	if (-not $il) { throw "reflection did not find a body for $TypeName.$MethodName" }
	$sha = [Security.Cryptography.SHA256]::Create()
	try { $digest = [BitConverter]::ToString($sha.ComputeHash($il)).Replace('-','').ToLowerInvariant() } finally { $sha.Dispose() }
	[pscustomobject]@{
		Token = [uint32]$method.MetadataToken
		Mvid = $method.Module.ModuleVersionId.ToString('D')
		Signature = $method.ReturnType.FullName + ' ' + $MethodName + '(' + (($method.GetParameters() | ForEach-Object { $_.ParameterType.FullName }) -join ',') + ')'
		IlSha256 = $digest
	}
}

try {
	if ([string]::IsNullOrWhiteSpace($MonoExe)) {
		throw 'This gate needs a Mono runtime to run its fixture under. Pass -MonoExe or set DGSPY_MONO_EXE; dgSpy does not ship or discover one.'
	}
	if (-not (Test-Path -LiteralPath $MonoExe)) { throw "No Mono runtime at $MonoExe." }
	if (-not (Test-Path -LiteralPath $targetExe)) {
		throw "The Mono HookLab fixture is not built: $targetExe. Build it: dotnet build tests\TestTargets\MonoHookLabTarget\MonoHookLabTarget.csproj -c Release"
	}
	New-Item -ItemType Directory -Force -Path $root | Out-Null
	Start-Transcript -LiteralPath (Join-Path $root 'driver.log') -Force | Out-Null

	if ($AgentPort -eq 0) {
		$probe = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0)
		$probe.Start(); $AgentPort = $probe.LocalEndpoint.Port; $probe.Stop()
	}
	$facts = Get-MethodFacts $targetExe 'MonoHookLabTarget.Program' 'Work'
	Write-Host "offline dnlib facts token=$($facts.Token) mvid=$($facts.Mvid) il_sha256=$($facts.IlSha256)"

	# suspend=n: the loop runs before the debugger arrives, so the gate observes a target that was
	# already working rather than one parked at its first method. That is the harder case and the real
	# one - a running Mono target is what an operator attaches to.
	$agent = "--debugger-agent=transport=dt_socket,server=y,address=127.0.0.1:$AgentPort,suspend=n"
	# MonoHost64 takes the runtime DLL first and hands everything after it to mono_main, which parses
	# its options before the assembly - so the agent argument sits in the same place either way.
	$arguments = if ($MonoRuntime) { @(('"'+$MonoRuntime+'"'),$agent,('"'+$targetExe+'"'),('"'+$root+'"')) }
		else { @($agent,('"'+$targetExe+'"'),('"'+$root+'"')) }
	if ($MonoRuntime) {
		if (-not (Test-Path -LiteralPath $MonoRuntime)) { throw "No Mono runtime library at $MonoRuntime." }
		Write-Host "mono_runtime=$MonoRuntime"
	}
	if ($MonoAssemblies) {
		if (-not (Test-Path -LiteralPath $MonoAssemblies)) { throw "No Mono assembly directory at $MonoAssemblies." }
		# Set on this process so Start-Process inherits it; the fixture's own runtime reads it.
		$env:MONO_PATH = $MonoAssemblies
		Write-Host "mono_assemblies=$MonoAssemblies"
	}
	$target = Start-Process -FilePath $MonoExe -ArgumentList $arguments `
		-WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $root 'target.out') -RedirectStandardError (Join-Path $root 'target.err')
	# Reading .Handle here caches it. Without that, a Start-Process -PassThru object cannot report
	# ExitCode once the process has gone, and the gate's last check - "the target exits cleanly" - reads
	# as undetermined forever. An undetermined exit code is not evidence that anything passed.
	$null = $target.Handle
	if (-not (Wait-Until { Test-Path -LiteralPath (Join-Path $root 'ready.txt') } 30)) { throw 'The Mono fixture did not report ready within 30 seconds.' }
	$targetFacts = @{}
	foreach ($line in Get-Content -LiteralPath (Join-Path $root 'facts.txt')) { $parts=$line -split '=',2; if($parts.Count -eq 2){ $targetFacts[$parts[0]] = $parts[1] } }
	Write-Host "target runtime=$($targetFacts['runtime'])"
	# The runtime's own answer, not the launcher's intent: a gate that assumed Mono because it invoked
	# mono.exe would keep passing if the launch silently fell through to the CLR.
	Check 'the fixture is running on Mono' ($targetFacts['runtime'] -like 'mono *') $targetFacts['runtime']
	Check 'the fixture ticks the unhooked value before anything attaches' (Wait-Until { (Behavior) -eq 42 } 15) ("behavior=" + (Behavior))
	# Observed, never connected to: a connect-close that does not complete the DWP handshake wedges the agent.
	Check "the Mono agent is listening on $AgentPort" (Wait-Until { @(Get-NetTCPConnection -State Listen -LocalPort $AgentPort -ErrorAction SilentlyContinue).Count -gt 0 } 30)

	$hostId = & (Join-Path $PSScriptRoot 'TestSupport\Start-DgSpyHost.ps1') -RpcPort $RpcPort -TargetFramework $TargetFramework
	$RpcPort = [int]$env:DGSPY_RPC_PORT

	$attached = Rpc 'attach_endpoint' @{ address='127.0.0.1'; port=$AgentPort; engine='mono'; process_is_suspended=$false; connection_timeout_ms=30000 }
	Check 'attach_endpoint opens a Mono session' ($attached.state -ne 'faulted') ("state=" + $attached.state + " " + $attached.fault_message)
	$sessionId = $attached.session_id
	$processId = @($attached.process_ids)[0]
	Check 'the session carries a target process' ($null -ne $processId) ($attached | ConvertTo-Json -Compress -Depth 4)

	# The refusal that used to stand here named the missing piece: "Owned internal breakpoints are
	# unavailable for this debugger engine". Readiness is asked before arrival so that a regression to
	# that state fails here, one step earlier and by name, rather than inside initialize_hooklab.
	$readiness = Rpc 'get_hooklab_readiness' @{ session_id=$sessionId; process_id=$processId }
	Write-Host ("readiness: " + ($readiness | ConvertTo-Json -Depth 4 -Compress))
	Check 'a Mono target is reported ready rather than refused' ($readiness.refuses -eq $false) ($readiness | ConvertTo-Json -Depth 4 -Compress)

	$modules = @((Rpc 'list_modules' @{ session_id=$sessionId; name_pattern='MonoHookLabTarget' }).modules)
	Check 'the fixture module is discovered' ($modules.Count -ge 1) ("count=" + $modules.Count)
	$moduleId = $modules[0].module_id
	$moduleName = $modules[0].name

	# Arrival is one evaluation, and Mono can only invoke on a suspended thread that has managed frames.
	# A freely running target offers neither, so the gate stops somewhere real first. CorDebug hides
	# this difference by hijacking a thread; the soft debugger cannot, which is why this section exists
	# here and not in the CLR v4 or CoreCLR gates.
	$breakpoint = Rpc 'set_breakpoint' @{ session_id=$sessionId; module=$moduleName; module_id=$moduleId; type='MonoHookLabTarget.Program'; method='Tick' }
	Check 'a breakpoint binds in the Mono target' ($breakpoint.bound_count -gt 0) ($breakpoint | ConvertTo-Json -Compress -Depth 5)
	$stop = Rpc 'wait_for_stop' @{ session_id=$sessionId; after_event_id=$breakpoint.cursor_event_id; timeout_ms=30000 }
	Check 'the loop reaches the carrier method' (-not $stop.timed_out -and @($stop.events).Count -gt 0) ($stop | ConvertTo-Json -Compress -Depth 4)
	# Cleared while stopped: the loop re-enters Tick every 100 ms, so leaving it bound would stop the
	# target again the moment anything resumed it.
	$null = Rpc 'clear_breakpoints' @{ session_id=$sessionId }

	$initialized = Rpc 'initialize_hooklab' @{ session_id=$sessionId; process_id=$processId } 120
	Write-Host ("initialize: " + ($initialized | ConvertTo-Json -Depth 4 -Compress))
	Check 'the resident arrives on Mono and its control channel verifies' `
		($initialized.initialized -eq $true -and $initialized.state -eq 'ready' -and $initialized.probe_instance_id) ($initialized | ConvertTo-Json -Compress -Depth 4)
	$status = Rpc 'get_hooklab_status' @{ session_id=$sessionId; process_id=$processId }
	Check 'the resident answers over its own control channel' ($status.initialized -and $status.runtimes.Count -eq 1) ($status | ConvertTo-Json -Compress -Depth 4)

	if (-not (Rpc 'get_session_state' @{ session_id=$sessionId }).is_running) { $null = Rpc 'continue' @{ session_id=$sessionId } }
	Check 'the target keeps ticking its unhooked value after the resident arrived' (Wait-Until { (Behavior) -eq 42 } 15) ("behavior=" + (Behavior))

	$hook = @{
		session_id=$sessionId; process_id=$processId; hook_id='mono-probe'; kind='Postfix'
		module_id=$moduleId; assembly='MonoHookLabTarget'; declaring_type='MonoHookLabTarget.Program'; method='Work'
		method_token=[int]$facts.Token; signature=$facts.Signature; module_mvid=$facts.Mvid; il_sha256=$facts.IlSha256
		revision=1; source='public static class MonoPostfixV1 { public static void Postfix(ref int __result) { __result = 777; } }'
	}
	$created = Rpc 'create_hook' $hook 120
	Check 'Roslyn compiles and the pinned Harmony installs a hook inside a Mono target' ($created.installed -and $created.hook.revision -eq 1) ($created | ConvertTo-Json -Compress -Depth 5)
	Check 'the hook changes live behaviour' (Wait-Until { (Behavior) -eq 777 } 20) ("behavior=" + (Behavior))

	$hook.revision = 2; $hook.source = 'public static class MonoPostfixV2 { public static void Postfix(ref int __result) { __result = 555; } }'
	$updated = Rpc 'update_hook' $hook 120
	Check 'a compiled revision atomically replaces the previous one' ($updated.installed -and $updated.hook.revision -eq 2) ($updated | ConvertTo-Json -Compress -Depth 5)
	Check 'revision 2 changes live behaviour' (Wait-Until { (Behavior) -eq 555 } 20) ("behavior=" + (Behavior))

	$removed = Rpc 'remove_hook' @{ session_id=$sessionId; process_id=$processId; hook_id='mono-probe' }
	Check 'remove reports ownership-scoped removal' $removed.removed ($removed | ConvertTo-Json -Compress -Depth 4)
	Check 'removing the hook restores the original behaviour' (Wait-Until { (Behavior) -eq 42 } 20) ("behavior=" + (Behavior))
	Check 'the resident inventory is empty after remove' ((Rpc 'list_hooks' @{ session_id=$sessionId; process_id=$processId }).hooks.Count -eq 0)

	$null = Rpc 'detach' @{ session_id=$sessionId } 30
	$sessionId = $null
	Check 'the Mono target survives detach' (-not $target.HasExited)
	Check 'and keeps working afterwards' (Wait-Until { (Behavior) -eq 42 } 10) ("behavior=" + (Behavior))

	# Retirement of the process itself, not of the resident: the resident stays loaded, which is what a
	# HookLab shutdown report says too. What matters here is that a target which has hosted a resident
	# and been detached from still exits cleanly under its own control.
	Set-Content -LiteralPath (Join-Path $root 'stop.txt') -Value 'stop' -Encoding Ascii
	$exited = $target.WaitForExit(30000)
	# The bounded overload leaves ExitCode unpopulated on a Start-Process -PassThru object. The argument-
	# less one returns immediately for a process that has already gone and fills it in, so the code is
	# read rather than guessed - an undetermined exit code must never be allowed to read as success.
	if ($exited) { $target.WaitForExit() }
	$exitCode = if ($exited) { $target.ExitCode } else { $null }
	Check 'the target exits cleanly when asked' ($exited -and $exitCode -eq 0) ("exited=" + $exited + " code=" + $(if($null -ne $exitCode){$exitCode}else{'undetermined'}))
}
catch {
	# Printed here, not left to the caller: Stop-Transcript runs in the finally below, so an exception
	# that escapes this script is written after the transcript has closed and lands nowhere at all. A
	# hidden-desktop run then reports a bare exit code over a log that simply stops.
	$script:fail++
	Write-Host ("FAIL  the gate did not finish: " + $_.Exception.Message) -ForegroundColor Red
	Write-Host ($_.ScriptStackTrace)
}
finally {
	if ($sessionId) { try { $null = Rpc 'detach' @{ session_id=$sessionId } 30 } catch { } }
	if ($target -and -not $target.HasExited) { Stop-Process -Id $target.Id -Force -ErrorAction SilentlyContinue }
	if ($hostId -gt 0) { Stop-Process -Id $hostId -Force -ErrorAction SilentlyContinue }
	Write-Host "Mono HookLab smoke: $pass passed, $fail failed" -ForegroundColor Cyan
	Write-Host "logs: $root"
	try { Stop-Transcript | Out-Null } catch { }
}
if ($fail -ne 0) { exit 1 }
exit 0
