# Proves HookLab arrival on a live Unity player, over the Mono soft debugger, from a composed layout.
#
# Unity is the special case here, not the subject. Mono is the runtime and a player merely embeds it,
# so the general gate is run-mono-hooklab-smoke.ps1: it drives this same lifecycle against a fixture
# this repository builds, under both mono-project's Mono and the runtime and byte-identical class
# libraries a Unity 2021.3 player carries. Everything downstream of arrival is covered faster still by
# the compatibility probe's `--runtime mono` leg.
#
# What only this script can add is a real player: a game loop, a mod loader, a graphics thread, and a
# process nobody wrote to be debugged. It needs a listening uch-debug-target player, which this repo
# neither builds nor ships - it takes a Unity editor and a licence. Launch it first:
#   .\tools\Launch-Target.ps1 -PlayerRoot .\build\release -Port 56000 -NoSuspend
param(
	[string]$AgentAddress = '127.0.0.1',
	# 56000, deliberately not the real game's 55555, so a live UCH can never be mistaken for this.
	[int]$AgentPort = 56000,
	# 0 takes a free ephemeral port. Windows reserves large TCP ranges for Hyper-V/WinNAT and the ranges
	# move between boots; a fixed port inside one binds with "an attempt was made to access a socket in a
	# way forbidden by its access permissions", which reads like a permissions problem and is not one.
	[int]$GatewayPort = 0,
	[int]$RpcPort = 0,
	[string]$TargetFramework = 'net10.0-windows'
)

$ErrorActionPreference = 'Stop'
$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new()
$repoRoot = Split-Path $PSScriptRoot -Parent
Push-Location $repoRoot

if ($GatewayPort -eq 0) {
	$probe = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
	$probe.Start(); $GatewayPort = $probe.LocalEndpoint.Port; $probe.Stop()
}
$gatewayUrl = "http://127.0.0.1:$GatewayPort"
# Windows PowerShell 5.1 is .NET Framework: no RandomNumberGenerator.GetBytes(int).
$token = [Guid]::NewGuid().ToString('N') + [Guid]::NewGuid().ToString('N')
$dnSpyHostId = $null; $gatewayProcess = $null
$script:activeSessionId = ''

# Behaviour is observed through a file the hook writes, exactly as run-mono-hooklab-smoke.ps1 does, and
# deliberately not through a breakpoint in the hooked method.
#
# Measured here: once Harmony patches TickLoop the debugger's breakpoint on it stops being reached,
# because the original body has been detoured. That is ordinary for a patched method and not a defect,
# but it makes "stop in the method and read a field" unusable as evidence that the patch worked - the
# very state being tested is the state that breaks the instrument.
#
# A file needs nothing from the debugger, nothing from func-eval, and only mscorlib from the hook.
function Clear-Behavior { Remove-Item -LiteralPath $script:behaviorPath -Force -ErrorAction SilentlyContinue }

function Wait-Behavior([string]$Expected, [int]$Seconds = 20) {
	$deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
	do {
		$value = Get-Content -LiteralPath $script:behaviorPath -ErrorAction SilentlyContinue | Select-Object -First 1
		if ($value -eq $Expected) { return $true }
		Start-Sleep -Milliseconds 200
	} until ([DateTime]::UtcNow -gt $deadline)
	return $false
}

# The negative form, and the one that makes "removed" mean something. A hook that has been removed must
# stop writing, so the file must still be absent after the player has had ample time to tick.
function Test-NoBehavior([int]$Seconds = 6) {
	Clear-Behavior
	Start-Sleep -Seconds $Seconds
	return -not (Test-Path -LiteralPath $script:behaviorPath)
}

function New-BehaviorHookSource([string]$ClassName, [string]$Value) {
	return 'public static class ' + $ClassName + ' { public static void Postfix() { System.IO.File.WriteAllText(@"' +
		$script:behaviorPath + '", "' + $Value + '"); } }'
}


. "$PSScriptRoot\TestSupport\McpClient.ps1"
Initialize-McpClient -GatewayUrl $gatewayUrl -Token $token

$runDirectory = Join-Path $env:TEMP ("dgspy-unity-hooklab-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $runDirectory -Force | Out-Null
# The hook writes here and the gate reads it, so a run cleans up after itself with the rest.
$script:behaviorPath = Join-Path $runDirectory 'behavior.txt'

try {
	Write-Section 'preflight'
	# Observe the agent, never connect to it. A connect-close without completing the DWP handshake wedges
	# the agent, and a bare connect while the runtime is still suspended awaiting its first debugger kills
	# the player outright. Both present as debugger bugs.
	$listening = @(Get-NetTCPConnection -State Listen -LocalPort $AgentPort -ErrorAction SilentlyContinue).Count -gt 0
	if (-not $listening) {
		throw "No listener on $AgentAddress`:$AgentPort. Launch the target first: tools\Launch-Target.ps1 -PlayerRoot <player> -Port $AgentPort -NoSuspend"
	}
	Assert-That "the Mono agent is listening on $AgentPort" $listening

	$configuredLayout = [Environment]::GetEnvironmentVariable('DGSPY_LAYOUT_ROOT')
	$gatewayDll = if (-not [string]::IsNullOrWhiteSpace($configuredLayout)) { Join-Path $configuredLayout 'bin\dgSpy.Gateway.dll' } else { Join-Path $repoRoot 'artifacts\layouts\local\bin\dgSpy.Gateway.dll' }
	if (-not (Test-Path $gatewayDll)) { throw "Gateway not found at $gatewayDll. Set DGSPY_LAYOUT_ROOT to a verified DgSpyTool layout." }

	$env:DGSPY_EXPORT_ROOT = $runDirectory
	$env:DGSPY_URL = $gatewayUrl
	$env:DGSPY_TOKEN = $token
	$dnSpyHostId = & "$PSScriptRoot\TestSupport\Start-DgSpyHost.ps1" -RpcPort $RpcPort -TargetFramework $TargetFramework
	$RpcPort = [int]$env:DGSPY_RPC_PORT
	$gatewayProcess = Start-Process -FilePath 'dotnet' -ArgumentList ('"' + $gatewayDll + '"') `
		-WorkingDirectory (Split-Path $gatewayDll) -WindowStyle Hidden -PassThru `
		-RedirectStandardOutput (Join-Path $runDirectory 'gateway.out') -RedirectStandardError (Join-Path $runDirectory 'gateway.err')

	if (-not (Wait-Until { try { $null -ne (Invoke-RestMethod ($gatewayUrl + '/health') -TimeoutSec 1) } catch { $false } } 30)) {
		throw 'Gateway health endpoint did not become ready.'
	}

	Write-Section 'attach to the Mono endpoint'
	$session = Invoke-Tool -Name 'attach_endpoint' -Arguments @{
		address = $AgentAddress; port = $AgentPort; engine = 'unity'
		process_is_suspended = $false; connection_timeout_ms = 30000
	}
	Assert-That 'attach_endpoint did not fault' ($session.state -ne 'faulted') "state=$($session.state) $($session.fault_message)"
	$script:activeSessionId = $session.session_id
	$processId = @($session.process_ids)[0]
	Assert-That 'the session carries a target process' ($null -ne $processId)

	Write-Section 'HookLab readiness on a Unity target'
	# A Unity target is now a supported HookLab target, not a refusal. What this script adds over
	# run-mono-hooklab-smoke.ps1 - which proves the same lifecycle against both Mono builds on a fixture
	# this repository builds - is the one thing a fixture cannot stand in for: a real player, with a real
	# game loop, a real mod loader, and a real graphics thread.
	$readiness = Invoke-Tool -Name 'get_hooklab_readiness' -Arguments @{ session_id = $script:activeSessionId; process_id = $processId }
	Write-Host ("  readiness: " + ($readiness | ConvertTo-Json -Depth 4 -Compress))
	Assert-That 'a Unity target is reported ready rather than refused' `
		($readiness.refuses -eq $false) ($readiness | ConvertTo-Json -Depth 4 -Compress)

	Write-Section 'reach a managed frame'
	# Arrival is one evaluation in the target, and Mono can only invoke on a suspended thread that has
	# managed frames. A freely running player offers neither, and initialize_hooklab refuses with
	# hooklab_no_carrier rather than inventing one - so the smoke has to stop the target somewhere real
	# first. CorDebug hides this difference by being able to hijack a thread; the soft debugger cannot.
	$harnessLoaded = Wait-Until {
		(Invoke-Tool -Name 'list_modules' -Arguments @{ session_id = $script:activeSessionId; name_pattern = 'Assembly-CSharp' }).total -gt 0
	} 60
	Assert-That 'the harness assembly is loaded' $harnessLoaded
	$modules = @((Invoke-Tool -Name 'list_modules' -Arguments @{ session_id = $script:activeSessionId; count = 500 }).modules)
	$harnessSymbol = @($modules | Where-Object { $_.name -like 'Assembly-CSharp*' })[0]
	$harnessModule = $harnessSymbol.name
	$harnessModuleId = $harnessSymbol.module_id
	$breakpoint = Invoke-MutatingTool -Name 'set_breakpoint' -Arguments @{
		session_id = $script:activeSessionId; module = $harnessModule
		type = 'UchDebugTarget.DebugTargetHarness'; method = 'TickLoop'
	}
	Assert-That 'the breakpoint is accepted for the harness' ($breakpoint.bound_count -gt 0) "payload=$($breakpoint | ConvertTo-Json -Compress -Depth 5)"
	$stop = Invoke-Tool -Name 'wait_for_stop' -Arguments @{
		session_id = $script:activeSessionId; after_event_id = $breakpoint.cursor_event_id; timeout_ms = 30000
	}
	Assert-That 'the harness loop reaches the breakpoint' (-not $stop.timed_out -and @($stop.events).Count -gt 0)

	# Cleared here, while the target is still stopped at it, and NOT left until the teardown below.
	#
	# Arrival resumes the target so the resident's worker can publish its completion report. A player's
	# loop re-enters TickLoop within milliseconds, so a breakpoint left armed stops the target again
	# immediately - and a stopped target cannot answer the control channel the host is about to open.
	# Measured: initialize_hooklab died on its 130-second deadline with the resident already reporting
	# status=ok, because the host was blocked in a pipe handshake the target could not answer.
	$null = Invoke-MutatingTool -Name 'clear_breakpoints' -Arguments @{ session_id = $script:activeSessionId }

	Write-Section 'initialize the resident (arrival by debugger evaluation)'
	# Arrival on a real player is the longest call in the suite: it stages a 16 MB payload, byte-loads it
	# and Roslyn inside the target, and then waits out its own 20 s completion deadline. The client's
	# ordinary 25 s HTTP wait expires while the operation is still running, which reads as a hang.
	$initialized = Invoke-MutatingTool -Name 'initialize_hooklab' -Arguments @{ session_id = $script:activeSessionId; process_id = $processId } -TimeoutSeconds 180
	Write-Host ("  initialize: " + ($initialized | ConvertTo-Json -Depth 4 -Compress))
	Assert-That 'the resident arrived and its control channel verified' ($initialized.initialized -eq $true) ($initialized | ConvertTo-Json -Depth 4 -Compress)

	Write-Section 'the resident answers over its own control channel'
	$status = Invoke-Tool -Name 'get_hooklab_status' -Arguments @{ session_id = $script:activeSessionId; process_id = $processId }
	Write-Host ("  status: " + ($status | ConvertTo-Json -Depth 4 -Compress))
	Assert-That 'the resident reports a probe identity' ($null -ne $status)

	Write-Section 'a compiled hook, inside a shipped player'
	# The part a fixture cannot stand in for, and the reason this section exists.
	#
	# The Mono lifecycle gate compiles and patches under the editor's unityjit-win32 profile, which has a
	# Facades directory. A shipped player has none, and nine non-Unity assemblies in total. So a compile
	# that reaches for anything outside that set passes there and fails here - which is exactly the
	# residual left by rewriting Roslyn's netstandard references onto their real net4x homes, two of
	# which (System.Xml.Linq, System.Runtime.Serialization) a player does not ship. This is the
	# measurement that closes that argument.
	$members = Invoke-Tool -Name 'list_members' -Arguments @{
		session_id = $script:activeSessionId; module = $harnessModule
		type = 'UchDebugTarget.DebugTargetHarness'; name_pattern = 'TickLoop'
	}
	$carrier = @($members.symbols | Where-Object { $_.name -like '*TickLoop*' })[0]
	Assert-That 'the carrier method is discoverable in the player' ($null -ne $carrier -and $null -ne $carrier.method_token) ($members | ConvertTo-Json -Compress -Depth 4)
	$template = Invoke-Tool -Name 'get_hook_template' -Arguments @{
		session_id = $script:activeSessionId; module_id = $harnessModuleId
		method_token = [int]$carrier.method_token; template = 'Postfix'
	}
	Assert-That 'the host derives the identity guards from the live module' `
		($null -ne $template.target -and $template.target.il_sha256) ($template | ConvertTo-Json -Compress -Depth 4)

	# Reflection rather than a typed __instance: typing it would make the hook source reference
	# Assembly-CSharp and, through MonoBehaviour, UnityEngine - so a compile failure could be about the
	# reference set rather than about the compiler. mscorlib alone keeps this about Roslyn running here.
	$hook = @{
		session_id = $script:activeSessionId; process_id = $processId; hook_id = 'unity-player-tick'; kind = 'Postfix'
		# The template describes the method, not the assembly that holds it, so the simple name comes from
		# the module list - the same place the breakpoint's module name came from.
		module_id = $harnessModuleId; assembly = [IO.Path]::GetFileNameWithoutExtension($harnessModule); declaring_type = $template.target.declaring_type
		method = $template.target.method; method_token = $template.target.method_token; signature = $template.target.signature
		module_mvid = $template.target.module_mvid; il_sha256 = $template.target.il_sha256; revision = 1
		source = New-BehaviorHookSource 'DgSpyPlayerPostfixV1' 'hooked-v1'
	}
	# The player must be running for its loop to reach the hook at all: arrival left the session paused.
	if (-not (Invoke-Tool -Name 'get_session_state' -Arguments @{ session_id = $script:activeSessionId }).is_running) {
		$null = Invoke-MutatingTool -Name 'continue' -Arguments @{ session_id = $script:activeSessionId }
	}
	Assert-That 'the unhooked player writes nothing' (Test-NoBehavior) 'something was already writing the behaviour file'

	$created = Invoke-MutatingTool -Name 'create_hook' -Arguments $hook -TimeoutSeconds 180
	Assert-That 'Roslyn compiles and the pinned Harmony patches inside a shipped player' `
		($created.installed -and $created.hook.revision -eq 1) ($created | ConvertTo-Json -Compress -Depth 5)
	Assert-That 'the hook changes live behaviour in the player' (Wait-Behavior 'hooked-v1' 30) 'the hooked player never wrote its sentinel'

	$hook.revision = 2
	$hook.source = New-BehaviorHookSource 'DgSpyPlayerPostfixV2' 'hooked-v2'
	Clear-Behavior
	$updated = Invoke-MutatingTool -Name 'update_hook' -Arguments $hook -TimeoutSeconds 180
	Assert-That 'a compiled revision atomically replaces the previous one' ($updated.installed -and $updated.hook.revision -eq 2) ($updated | ConvertTo-Json -Compress -Depth 5)
	Assert-That 'revision 2 changes live behaviour' (Wait-Behavior 'hooked-v2' 30) 'the revision-2 sentinel never appeared'

	$removed = Invoke-MutatingTool -Name 'remove_hook' -Arguments @{ session_id = $script:activeSessionId; process_id = $processId; hook_id = 'unity-player-tick' }
	Assert-That 'remove reports ownership-scoped removal' $removed.removed ($removed | ConvertTo-Json -Compress -Depth 4)
	Assert-That 'removing the hook restores the original behaviour' (Test-NoBehavior) 'the removed hook was still writing'
	Assert-That 'the resident inventory is empty after remove' `
		(@((Invoke-Tool -Name 'list_hooks' -Arguments @{ session_id = $script:activeSessionId; process_id = $processId }).hooks).Count -eq 0)

	Write-Section 'detach, and the target survives'
	$null = Invoke-MutatingTool -Name 'clear_breakpoints' -Arguments @{ session_id = $script:activeSessionId }
	$null = Invoke-MutatingTool -Name 'continue' -Arguments @{ session_id = $script:activeSessionId }
	$null = Invoke-MutatingTool -Name 'detach' -Arguments @{ session_id = $script:activeSessionId }
	$script:activeSessionId = ''
	Start-Sleep -Seconds 2
	Assert-That 'the player is still running after detach' ($null -ne (Get-Process -Id $processId -ErrorAction SilentlyContinue))
}
finally {
	if ($script:activeSessionId) { try { $null = Invoke-MutatingTool -Name 'detach' -Arguments @{ session_id = $script:activeSessionId } } catch { } }
	if ($gatewayProcess) { Stop-Process -Id $gatewayProcess.Id -Force -ErrorAction SilentlyContinue }
	if ($dnSpyHostId) { Stop-Process -Id $dnSpyHostId -Force -ErrorAction SilentlyContinue }
	Pop-Location
}

Write-Host ''
if ($script:failures.Count -gt 0) {
	Write-Host "FAILED  $($script:failures.Count) of $script:checks checks" -ForegroundColor Red
	$script:failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
	Write-Host "Logs: $runDirectory"
	exit 1
}
Write-Host "PASSED  $script:checks checks" -ForegroundColor Green
exit 0
