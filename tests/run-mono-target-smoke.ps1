<#
.SYNOPSIS
	Live Mono/Unity smoke against the uch-debug-target player.

.DESCRIPTION
	The CorDebug smoke covers the .NET Framework engine. Nothing covered Mono/Unity, which is how a
	race in caller-selected thread resolution survived: the CorDebug gate asserted "get_frame returns
	the caller-selected frame" and passed throughout, because reproducing it needed a stop thread
	distinct from the requested one and a slower current-thread assignment than CorDebug shows.

	The target is built and published from batram/uch-debug-target. It reproduces Ultimate Chicken
	Horse's runtime arrangement: Unity 2021.3.45f1, Mono2x, x64, with a release runtime made
	debuggable by the same dnSpy Unity mono patch the real game carries. Its patched
	mono-2.0-bdwgc.dll is byte-identical to UCH's.

	This script attaches to an already-listening agent. Launching the player is the caller's job
	(tools\Launch-Target.ps1 in the target repo), because the launcher knows things this repo should
	not have to: Unity silently ignores a malformed --debugger-agent, and readiness must be observed
	rather than connected to.

	ASCII-only by policy: an em dash in a .ps1 parses as a stray quote under CP1252.

.EXAMPLE
	# In the target repo:  .\tools\Launch-Target.ps1 -PlayerRoot .\build\release -Port 56000
	.\tests\run-mono-target-smoke.ps1 -AgentPort 56000
#>
[CmdletBinding()]
param(
	[string]$AgentAddress = '127.0.0.1',
	# 56000, deliberately not the real game's 55555, so a live UCH can never be mistaken for this.
	[int]$AgentPort = 56000,
	# Launch-Target defaults to suspend=y: the runtime blocks before running managed code.
	[bool]$AgentSuspended = $true,
	[ValidateSet('net10.0-windows','net48')][string]$TargetFramework = 'net10.0-windows',
	[int]$GatewayPort = 17360,
	[int]$RpcPort = 7361
)

$ErrorActionPreference = 'Stop'
$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new()
$repoRoot = Split-Path $PSScriptRoot -Parent
Push-Location $repoRoot

$gatewayUrl = "http://127.0.0.1:$GatewayPort"
# Windows PowerShell 5.1 is .NET Framework: no RandomNumberGenerator.GetBytes(int).
$token = [Guid]::NewGuid().ToString('N') + [Guid]::NewGuid().ToString('N')
$dnSpyHostId = $null; $gatewayProcess = $null
$script:activeSessionId = ''

. "$PSScriptRoot\TestSupport\McpClient.ps1"
Initialize-McpClient -GatewayUrl $gatewayUrl -Token $token

$runDirectory = Join-Path $env:TEMP ("dgspy-mono-smoke-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $runDirectory -Force | Out-Null

try {
	Write-Section 'preflight'
	# Observe the agent, never connect to it. A connect-close without completing the DWP handshake
	# wedges the agent, and a bare connect while the runtime is still suspended awaiting its first
	# debugger kills the player outright. Both present as debugger bugs.
	$listening = @(Get-NetTCPConnection -State Listen -LocalPort $AgentPort -ErrorAction SilentlyContinue).Count -gt 0
	if (-not $listening) {
		throw "No listener on $AgentAddress`:$AgentPort. Launch the target first: tools\Launch-Target.ps1 -PlayerRoot <player> -Port $AgentPort"
	}
	Assert-That "the Mono agent is listening on $AgentPort" $listening

	$gatewayDll = Join-Path $repoRoot 'dgSpy.Gateway\bin\Release\net10.0\dgSpy.Gateway.dll'
	if (-not (Test-Path $gatewayDll)) { throw "Gateway not built at $gatewayDll. Run .\build-dgspy.ps1." }

	$env:DGSPY_EXPORT_ROOT = $runDirectory
	$env:DGSPY_URL = $gatewayUrl
	$env:DGSPY_TOKEN = $token
	$dnSpyHostId = & "$PSScriptRoot\TestSupport\Start-DgSpyHost.ps1" -RpcPort $RpcPort -TargetFramework $TargetFramework
	$gatewayProcess = Start-Process -FilePath 'dotnet' -ArgumentList ('"' + $gatewayDll + '"') `
		-WorkingDirectory (Split-Path $gatewayDll) -WindowStyle Hidden -PassThru `
		-RedirectStandardOutput (Join-Path $runDirectory 'gateway.out') -RedirectStandardError (Join-Path $runDirectory 'gateway.err')

	if (-not (Wait-Until { try { $null -ne (Invoke-RestMethod ($gatewayUrl + '/health') -TimeoutSec 1) } catch { $false } } 30)) {
		throw 'Gateway health endpoint did not become ready.'
	}

	Write-Section 'attach to the Mono endpoint'
	# list_programs cannot see this target: server=y means it listens rather than announcing, so
	# there is no discovery beacon. attach_endpoint exists for exactly that.
	$session = Invoke-Tool -Name 'attach_endpoint' -Arguments @{
		address = $AgentAddress; port = $AgentPort; engine = 'unity'
		process_is_suspended = $AgentSuspended; connection_timeout_ms = 30000
	}
	Assert-That 'attach_endpoint did not fault' ($session.state -ne 'faulted') "state=$($session.state) $($session.fault_message)"
	$script:activeSessionId = $session.session_id
	Assert-That 'the session carries a target process' (@($session.process_ids).Count -gt 0)

	Write-Section 'engine identity'
	$hostInfo = Invoke-Tool -Name 'get_host_info' -Arguments @{}
	Assert-That 'the host advertises the unity engine' ($hostInfo.engines -contains 'unity')
	$capabilities = Invoke-Tool -Name 'get_capabilities' -Arguments @{}
	Assert-That 'capabilities separate Mono sequence points from CorDebug offsets' ($null -ne $capabilities)

	Write-Section 'reach the harness'
	# Launch-Target defaults to suspend=y, so the runtime is parked BEFORE executing any managed
	# code: Assembly-CSharp is not loaded and no thread has managed frames yet. Inspecting here
	# proves nothing. Drive the target to a known point first, which also exercises Mono breakpoint
	# binding -- Mono binds only at sequence points, unlike CorDebug.
	$resumedToRun = Invoke-MutatingTool -Name 'continue' -Arguments @{}
	Assert-That 'the target starts running managed code' ($resumedToRun.state -eq 'running') "state=$($resumedToRun.state)"

	$harnessLoaded = Wait-Until {
		(Invoke-Tool -Name 'list_modules' -Arguments @{ session_id = $script:activeSessionId; name_pattern = 'Assembly-CSharp' }).total -gt 0
	} 60
	Assert-That 'the harness assembly loads once the target runs' $harnessLoaded

	Write-Section 'modules'
	# ModuleCreator plus AssemblyMirror.GetMetadataBlob and .IsDynamic, which only exist in dnSpyEx's
	# Mono.Debugger.Soft fork. On the fork this repo used to pin, this section could not even compile.
	$modules = @((Invoke-Tool -Name 'list_modules' -Arguments @{ session_id = $script:activeSessionId; count = 500 }).modules)
	Assert-That 'the target reports loaded modules' ($modules.Count -gt 0) "count=$($modules.Count)"
	Assert-That 'the harness assembly is loaded' (@($modules | Where-Object { $_.name -like 'Assembly-CSharp*' }).Count -gt 0)
	Assert-That 'mscorlib is loaded' (@($modules | Where-Object { $_.name -like 'mscorlib*' }).Count -gt 0)
	Assert-That 'every module reports breakpoint capability explicitly' (@($modules | Where-Object { $null -eq $_.can_set_breakpoint }).Count -eq 0)

	Write-Section 'breakpoint on the harness loop'
	$harnessModule = @($modules | Where-Object { $_.name -like 'Assembly-CSharp*' })[0].name
	$breakpoint = Invoke-MutatingTool -Name 'set_breakpoint' -Arguments @{
		session_id = $script:activeSessionId; module = $harnessModule
		type = 'UchDebugTarget.DebugTargetHarness'; method = 'TickLoop'
	}
	Assert-That 'the breakpoint binds in the harness' ($breakpoint.bound) "bound=$($breakpoint.bound) msg='$($breakpoint.message)'"
	Assert-That 'the breakpoint returns a cursor' ($null -ne $breakpoint.cursor_event_id)

	$stop = Invoke-Tool -Name 'wait_for_stop' -Arguments @{
		session_id = $script:activeSessionId; after_event_id = $breakpoint.cursor_event_id; timeout_ms = 30000
	}
	Assert-That 'the harness loop reaches the breakpoint' (-not $stop.timed_out -and @($stop.events).Count -gt 0)

	Write-Section 'threads and stacks'
	$paused = Invoke-Tool -Name 'get_session_state' -Arguments @{ session_id = $script:activeSessionId }
	Assert-That 'the target is stopped at the breakpoint' ($paused.state -eq 'paused') "state=$($paused.state)"
	$threads = @(Invoke-Tool -Name 'list_threads' -Arguments @{ session_id = $script:activeSessionId })
	Assert-That 'threads are enumerated' ($threads.Count -gt 0)
	Assert-That 'thread ids are unique' ((@($threads.thread_id | Sort-Object -Unique)).Count -eq $threads.Count)

	# Every thread with managed frames must resolve them. This is ThreadMirror.GetFrames, where a
	# dgSpy patch bounds a wait Unity can otherwise leave outstanding forever.
	$withFrames = $null
	foreach ($thread in $threads) {
		$stack = @(Invoke-Tool -Name 'get_callstack' -Arguments @{ session_id = $script:activeSessionId; thread_id = $thread.thread_id; max_frames = 20 })
		if ($stack.Count -gt 0) { $withFrames = @{ Thread = $thread; Stack = $stack }; break }
	}
	Assert-That 'at least one thread has a managed call stack' ($null -ne $withFrames)

	if ($null -ne $withFrames) {
		$threadId = $withFrames.Thread.thread_id
		Assert-That 'every frame names a method' (@($withFrames.Stack | Where-Object { [string]::IsNullOrWhiteSpace($_.name) }).Count -eq 0)
		Assert-That 'every frame carries an IL offset' (@($withFrames.Stack | Where-Object { $null -eq $_.il_offset }).Count -eq 0)

		Write-Section 'caller-selected frames'
		# The regression that motivated this file. CaptureFrameAsync used to assign the current thread
		# and then read it back to learn which thread it had picked, so the first call after a pause
		# could silently answer from the thread that carried the stop instead.
		$deepest = [Math]::Min($withFrames.Stack.Count - 1, 5)
		$frame = Invoke-Tool -Name 'get_frame' -Arguments @{
			session_id = $script:activeSessionId; thread_id = $threadId; frame_index = $deepest; include = @('locals','this')
		}
		Assert-That 'get_frame answers on the requested thread' ($frame.thread_id -eq $threadId) "asked $threadId, got $($frame.thread_id)"
		Assert-That 'get_frame answers at the requested index' ($frame.frame_index -eq $deepest)
		Assert-That 'the returned frame matches the call stack' ($frame.name -eq $withFrames.Stack[$deepest].name)
	}

	Write-Section 'resume and detach'
	# TickLoop is a loop, so leaving the breakpoint set would stop the target again immediately and
	# detach would be racing a fresh stop.
	Invoke-MutatingTool -Name 'clear_breakpoints' -Arguments @{} | Out-Null
	$resumed = Invoke-MutatingTool -Name 'continue' -Arguments @{}
	Assert-That 'the target resumes' ($resumed.state -eq 'running') "state=$($resumed.state)"

	$state = Invoke-Tool -Name 'get_session_state' -Arguments @{ session_id = $script:activeSessionId }
	$detached = Invoke-Tool -Name 'detach' -Arguments @{ session_id = $script:activeSessionId; expected_lifecycle_version = $state.lifecycle_version }
	Assert-That 'detach reports detached, not terminated' ($detached.detached -eq $true -and $detached.terminated -ne $true)
	$script:activeSessionId = ''

	# The player must outlive the debugger. Observe it, do not connect to it.
	Start-Sleep -Seconds 2
	Assert-That 'the player survives detach' (@(Get-NetTCPConnection -State Listen -LocalPort $AgentPort -ErrorAction SilentlyContinue).Count -gt 0)
}
finally {
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
