# End-to-end test for the dgSpy milestone 1 slice and completed Phase 3 event surface.
# Builds and deploys via build-dgspy.ps1, starts a disposable target, dnSpy and the gateway, then
# drives the whole MCP surface and asserts on the results. Everything it starts, it stops.
param(
	[int]$GatewayPort = 17350,
	[int]$RpcPort = 0
)

$ErrorActionPreference = 'Stop'
$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new()

$repoRoot = Split-Path $PSScriptRoot -Parent
$gatewayUrl = "http://127.0.0.1:$GatewayPort"
if ($RpcPort -eq 0) {
	$probe = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
	$probe.Start(); $RpcPort = ([Net.IPEndPoint]$probe.LocalEndpoint).Port; $probe.Stop()
}

$dnSpyDir = Join-Path $repoRoot 'dnSpy\dnSpy\bin\Release\net48'
$gatewayDll = Join-Path $repoRoot 'dgSpy.Gateway\bin\Release\net7.0\dgSpy.Gateway.dll'
$targetProject = Join-Path $PSScriptRoot 'TestTargets\Milestone1Target\Milestone1Target.csproj'
$targetExe = Join-Path $PSScriptRoot 'TestTargets\Milestone1Target\bin\Debug\net48\Milestone1Target.exe'
$runDirectory = Join-Path ([IO.Path]::GetTempPath()) ('dgspy-smoke-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $runDirectory | Out-Null

# Windows PowerShell 5.1 is .NET Framework: no RandomNumberGenerator.GetBytes(int), no Convert.ToHexString.
$token = [Guid]::NewGuid().ToString('N') + [Guid]::NewGuid().ToString('N')
$dnSpyProcess = $null; $gatewayProcess = $null; $targetProcess = $null; $detachTargetProcess = $null
$script:requestId = 0
$script:failures = @()
$script:checks = 0

function Assert-That {
	# $Condition stays untyped: -match/-like against a collection yield the matches rather than a
	# boolean, and a [bool] parameter would throw instead of failing the check.
	param([string]$What, $Condition, [string]$Detail = '')
	$script:checks++
	if (@($Condition).Count -gt 0 -and [bool](@($Condition) | Select-Object -Last 1)) { Write-Host "  PASS  $What" -ForegroundColor DarkGreen }
	else {
		Write-Host "  FAIL  $What $Detail" -ForegroundColor Red
		$script:failures += "$What $Detail"
	}
}

function Invoke-Mcp {
	param([string]$Method, [hashtable]$Parameters, [hashtable]$Headers, [switch]$Raw)
	$script:requestId++
	$body = @{ jsonrpc = '2.0'; id = $script:requestId; method = $Method }
	if ($null -ne $Parameters) { $body.params = $Parameters }
	if ($null -eq $Headers) { $Headers = @{ 'X-dgSpy-Token' = $token } }
	$response = Invoke-RestMethod -Uri ($gatewayUrl + '/mcp') -Method Post -ContentType 'application/json' `
		-Headers $Headers -TimeoutSec 25 -Body ($body | ConvertTo-Json -Depth 12)
	if ($Raw) { return $response }
	if ($null -ne $response.error) { throw ('MCP error: ' + ($response.error | ConvertTo-Json -Compress)) }
	return $response.result
}

# Tools return their payload as JSON text; parse that rather than relying on structuredContent shape.
function Invoke-Tool {
	# -AsText returns the raw JSON. Prefer it for emptiness checks: ConvertFrom-Json collapses an
	# empty array in ways that make .Count unreliable in Windows PowerShell.
	param([string]$Name, [hashtable]$Arguments, [switch]$ExpectError, [switch]$AsText)
	$result = Invoke-Mcp -Method 'tools/call' -Parameters @{ name = $Name; arguments = $Arguments }
	if ($ExpectError) {
		if (-not $result.isError) { throw "Tool $Name was expected to fail but succeeded." }
		return $result.content[0].text
	}
	if ($result.isError) { throw ("Tool $Name failed: " + $result.content[0].text) }
	if ($AsText) { return $result.content[0].text }
	return $result.content[0].text | ConvertFrom-Json
}

function Wait-Until {
	param([scriptblock]$Condition, [int]$TimeoutSeconds = 30)
	$deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
	do {
		Start-Sleep -Milliseconds 250
		if (& $Condition) { return $true }
	} until ([DateTime]::UtcNow -gt $deadline)
	return $false
}

try {
	Write-Host "== build and deploy ==" -ForegroundColor Cyan
	& (Join-Path $repoRoot 'build-dgspy.ps1') -DnSpyDir $dnSpyDir | Out-Null
	& dotnet build $targetProject -c Debug --nologo -v:quiet
	if ($LASTEXITCODE) { throw "Target build failed with exit code $LASTEXITCODE" }

	Write-Host "== start target, dnSpy, gateway ==" -ForegroundColor Cyan
	$targetOut = Join-Path $runDirectory 'target.out'
	$targetProcess = Start-Process -FilePath $targetExe -WindowStyle Hidden -PassThru `
		-RedirectStandardOutput $targetOut -RedirectStandardError (Join-Path $runDirectory 'target.err')
	if (-not (Wait-Until { @(Get-Content $targetOut -ErrorAction SilentlyContinue).Count -ge 2 } 15)) {
		throw 'The test target did not publish its PID and method token.'
	}
	$targetLines = Get-Content $targetOut
	$targetId = [int](($targetLines | Where-Object { $_ -like 'PID=*' }) -replace '^PID=', '')
	$methodToken = [uint32](($targetLines | Where-Object { $_ -like 'TOKEN=*' }) -replace '^TOKEN=', '')

	$env:DGSPY_RPC_PORT = [string]$RpcPort
	$env:DGSPY_EXPORT_ROOT = $runDirectory
	$env:DGSPY_URL = $gatewayUrl
	$env:DGSPY_TOKEN = $token
	$dnSpyProcess = Start-Process -FilePath (Join-Path $dnSpyDir 'dnSpy.exe') -ArgumentList '--multiple','--dgspy-no-window-activation' `
		-WorkingDirectory $dnSpyDir -WindowStyle Hidden -PassThru
	$gatewayProcess = Start-Process -FilePath 'dotnet' -ArgumentList ('"' + $gatewayDll + '"') `
		-WorkingDirectory (Split-Path $gatewayDll) -WindowStyle Hidden -PassThru `
		-RedirectStandardOutput (Join-Path $runDirectory 'gateway.out') -RedirectStandardError (Join-Path $runDirectory 'gateway.err')

	if (-not (Wait-Until { try { $null -ne (Invoke-RestMethod ($gatewayUrl + '/health') -TimeoutSec 1) } catch { $false } } 30)) {
		throw 'Gateway health endpoint did not become ready.'
	}
	if (-not (Wait-Until { @(Get-NetTCPConnection -State Listen -LocalPort $RpcPort -ErrorAction SilentlyContinue).Count -gt 0 } 40)) {
		throw "Extension RPC endpoint 127.0.0.1:$RpcPort did not become ready. Is the extension deployed?"
	}

	Write-Host "== gateway access control ==" -ForegroundColor Cyan
	$rejected = $false
	try { Invoke-Mcp -Method 'tools/list' -Parameters @{} -Headers @{ 'X-dgSpy-Token' = $token; 'Origin' = 'https://evil.example' } | Out-Null }
	catch { $rejected = $_.Exception.Response.StatusCode.value__ -eq 403 }
	Assert-That 'a cross-origin request is refused' $rejected

	$rejected = $false
	try { Invoke-Mcp -Method 'tools/list' -Parameters @{} -Headers @{} | Out-Null }
	catch { $rejected = $_.Exception.Response.StatusCode.value__ -eq 403 }
	Assert-That 'a request without the token is refused' $rejected

	# Loopback binding is the extension RPC endpoint's only protection, so prove it is real rather than
	# trusting the constructor argument. Connect to this machine's own LAN address on the RPC port: the
	# listener is not bound there, so the stack refuses it.
	$localAddress = (Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
		Where-Object { $_.IPAddress -ne '127.0.0.1' -and $_.IPAddress -notlike '169.254.*' } |
		Select-Object -First 1).IPAddress
	if ($localAddress) {
		$reachable = $false
		try {
			$probeClient = [Net.Sockets.TcpClient]::new()
			$reachable = $probeClient.ConnectAsync($localAddress, $RpcPort).Wait(2000) -and $probeClient.Connected
		} catch { $reachable = $false } finally { if ($probeClient) { $probeClient.Dispose() } }
		Assert-That "the extension RPC port is unreachable on $localAddress" (-not $reachable)
	}
	else { Write-Host "  SKIP  no non-loopback IPv4 address to probe" -ForegroundColor DarkYellow }

	# The gateway opens a fresh connection per request and holds no debugger state, so a dnSpy restart
	# must be invisible to the next tool call. Do this before attaching: closing dnSpy with a session
	# attached terminates the target.
	Write-Host "== gateway survives a dnSpy restart ==" -ForegroundColor Cyan
	Stop-Process -Id $dnSpyProcess.Id -Force
	$dnSpyProcess.WaitForExit(10000) | Out-Null
	$failedWhileDown = $false
	try { Invoke-Tool -Name 'get_host_info' -Arguments @{} | Out-Null } catch { $failedWhileDown = $true }
	Assert-That 'a tool call fails while dnSpy is down' $failedWhileDown
	$dnSpyProcess = Start-Process -FilePath (Join-Path $dnSpyDir 'dnSpy.exe') -ArgumentList '--multiple','--dgspy-no-window-activation' `
		-WorkingDirectory $dnSpyDir -WindowStyle Hidden -PassThru
	if (-not (Wait-Until { @(Get-NetTCPConnection -State Listen -LocalPort $RpcPort -ErrorAction SilentlyContinue).Count -gt 0 } 60)) {
		throw "Extension RPC endpoint did not come back after the dnSpy restart."
	}
	$reconnected = Invoke-Tool -Name 'get_host_info' -Arguments @{}
	Assert-That 'the gateway reconnects after a dnSpy restart without being restarted itself' ($reconnected.host_id -eq 'local')

	Write-Host "== host info and capabilities ==" -ForegroundColor Cyan
	Assert-That 'get_host_info reports this machine and a live connection' ($reconnected.machine_name -eq $env:COMPUTERNAME -and $reconnected.connection_state -eq 'connected')
	Assert-That 'get_host_info reports an x64 dnSpy' ($reconnected.architecture -eq 'X64') "(was $($reconnected.architecture))"
	Assert-That 'get_host_info reports both in-scope engines' (@($reconnected.engines) -contains 'cordebug' -and @($reconnected.engines) -contains 'unity')
	Assert-That 'get_host_info reports no session before attach' ($null -eq $reconnected.session_id)
	Assert-That 'get_host_info names a dnSpy version' (-not [string]::IsNullOrWhiteSpace($reconnected.dnspy_version)) "(was '$($reconnected.dnspy_version)')"

	$capabilities = Invoke-Tool -Name 'get_capabilities' -Arguments @{}
	Assert-That 'get_capabilities matches the protocol version the gateway reports' ($capabilities.protocol_version -eq (Invoke-RestMethod ($gatewayUrl + '/health')).protocol_version)
	$cordebug = @($capabilities.engines) | Where-Object { $_.engine -eq 'cordebug' }
	$unity = @($capabilities.engines) | Where-Object { $_.engine -eq 'unity' }
	Assert-That 'capabilities separate CorDebug offsets from Mono sequence points' ($cordebug.arbitrary_il_offset_breakpoints -and $unity.sequence_point_breakpoints_only)
	Assert-That 'capabilities say the Unity engine is not discoverable' (-not $unity.discoverable -and @($unity.acquisition) -contains 'attach_endpoint')
	Assert-That 'capabilities admit that a deadline cannot abort in-flight work' ($capabilities.limits.cancels_in_flight_work -eq $false)
	Assert-That 'capabilities bound every operation' (@(@($capabilities.operations) | Where-Object { $_.max_duration_ms -le 0 }).Count -eq 0)
	# The kinds filter is caller-supplied, so the vocabulary has to be discoverable rather than guessed.
	Assert-That 'capabilities advertise the event-kind vocabulary' (@($capabilities.event_kinds) -contains 'stopped' -and @($capabilities.event_kinds) -contains 'breakpoint_hit')
	Assert-That 'capabilities advertise the stop-reason vocabulary' (@($capabilities.stop_reasons) -contains 'breakpoint' -and @($capabilities.stop_reasons) -contains 'unknown')
	Assert-That 'capabilities advertise the Phase 4 vocabularies' (@($capabilities.step_kinds) -contains 'over' -and @($capabilities.condition_kinds) -contains 'when_changed' -and @($capabilities.hit_count_kinds) -contains 'at_least')

	Write-Host "== discovery ==" -ForegroundColor Cyan
	$tools = @((Invoke-Mcp -Method 'tools/list' -Parameters @{}).tools | ForEach-Object { $_.name })
	foreach ($expected in 'get_host_info','get_capabilities','list_programs','attach','attach_endpoint','launch','detach','terminate','restart','list_sessions','get_session_state','pause','continue','set_il_breakpoint','list_breakpoints','remove_breakpoint','clear_breakpoints','wait_for_stop','wait_for_event','get_events','get_stop_reason','list_threads','get_callstack','get_frame','update_breakpoint','set_exception_breakpoint','list_exception_breakpoints','step_into','step_over','step_out','evaluate','get_members','set_value','get_exception','add_watch','list_watches','remove_watch','list_modules','list_documents','list_types','list_members','search_symbols','get_il','get_csharp','search_text','find_references','find_implementations','get_metadata','get_raw_module','set_breakpoint','invoke_method','create_object','read_memory','write_memory','get_disassembly','get_registers','set_instruction_pointer','create_object_id','list_object_ids','evaluate_object_id','release_object_id','get_autos','get_output','wait_for_output','set_module_breakpoint','list_module_breakpoints','update_module_breakpoint','remove_module_breakpoint','export_breakpoints','import_breakpoints','list_exception_categories','list_exception_policies','set_exception_policy','remove_exception_policy','restore_exception_defaults','get_value_export','write_value_export','analyze_symbol') {
		Assert-That "tools/list advertises $expected" ($tools -contains $expected)
	}

	$watch = [Diagnostics.Stopwatch]::StartNew()
	$programs = @(Invoke-Tool -Name 'list_programs' -Arguments @{ process_ids = @($targetId) })
	$filteredMs = $watch.ElapsedMilliseconds
	Assert-That 'a pid-filtered listing finds the target' ($programs.Count -ge 1 -and $programs[0].pid -eq $targetId)
	Assert-That 'a pid-filtered listing is fast' ($filteredMs -lt 1000) "(took ${filteredMs}ms)"
	$program = $programs[0]
	Assert-That 'the .NET Framework test target is x64' ($program.architecture -eq 'X64') "(was $($program.architecture))"
	Assert-That 'program_id carries no dnSpy type name' (-not $program.program_id.Contains('RuntimeId')) "(was $($program.program_id))"
	Assert-That 'runtime_guid is distinct from runtime_kind_guid' ($program.runtime_guid -ne $program.runtime_kind_guid)
	Assert-That 'the entry names an attach provider a caller can pass back' (@($program.attach_providers) -contains 'DotNetFramework') "(was $($program.attach_providers -join ','))"

	# Provider selection is what keeps a caller from paying for scans it does not need. Naming the
	# provider that owns this target must still find it; naming only the Unity providers must not.
	$byProvider = @(Invoke-Tool -Name 'list_programs' -Arguments @{ process_ids = @($targetId); provider_names = @('DotNetFramework') })
	Assert-That 'a provider-filtered listing still finds the target' (@($byProvider | Where-Object { $_.pid -eq $targetId }).Count -ge 1)
	$wrongProvider = Invoke-Tool -Name 'list_programs' -Arguments @{ process_ids = @($targetId); provider_names = @('UnityEditor') } -AsText
	Assert-That 'selecting only Unity providers excludes a .NET Framework target' (-not $wrongProvider.Contains("`"pid`":$targetId")) "(payload $wrongProvider)"

	# Each listing replaces the set of valid program_id values, and the one above deliberately returned
	# none. Re-establish the cache from a listing that contains the target before attaching to it.
	$program = @(Invoke-Tool -Name 'list_programs' -Arguments @{ process_ids = @($targetId) })[0]

	Write-Host "== attach and session tracking ==" -ForegroundColor Cyan
	$session = Invoke-Tool -Name 'attach' -Arguments @{ program_id = $program.program_id }
	$sessionId = $session.session_id
	Assert-That 'attach reports a live state, not exited' ($session.state -in @('running','paused')) "(was $($session.state))"
	Assert-That 'attach reports the attached process' ($session.process_ids -contains $targetId)

	$sessions = @(Invoke-Tool -Name 'list_sessions' -Arguments @{})
	Assert-That 'list_sessions recovers the session' ($sessions.Count -eq 1 -and $sessions[0].session_id -eq $sessionId)
	Assert-That 'list_sessions reports the attached program' ($sessions[0].program_id -eq $program.program_id)
	Assert-That 'list_sessions says detaching is safe' ($sessions[0].can_detach_without_terminating)

	Write-Host "== multiple active targets ==" -ForegroundColor Cyan
	$second = Invoke-Tool -Name 'launch' -Arguments @{ filename = $targetExe; engine = 'cordebug' }
	$secondPid = [int](@($second.process_ids | Where-Object { $_ -ne $targetId }) | Select-Object -First 1)
	Assert-That 'launch adds a second process to the same logical session' ($second.session_id -eq $sessionId -and @($second.process_ids).Count -eq 2 -and $secondPid -gt 0) "(ids=$($second.process_ids -join ','))"
	$beforeAmbiguous = Invoke-Tool -Name 'get_session_state' -Arguments @{ session_id = $sessionId }
	$ambiguousPause = Invoke-Tool -Name 'pause' -Arguments @{ session_id = $sessionId } -ExpectError
	$afterAmbiguous = Invoke-Tool -Name 'get_session_state' -Arguments @{ session_id = $sessionId }
	Assert-That 'an unselected multi-target pause returns ambiguous_target' ($ambiguousPause -match 'More than one process|process_id') "(error='$ambiguousPause')"
	Assert-That 'an ambiguous pause performs no debugger action' ($beforeAmbiguous.state -eq 'running' -and $afterAmbiguous.state -eq 'running') "(before=$($beforeAmbiguous.state) after=$($afterAmbiguous.state))"
	$selectedPause = Invoke-Tool -Name 'pause' -Arguments @{ session_id = $sessionId; process_id = $secondPid }
	Assert-That 'a selected process can pause independently' ($selectedPause.state -eq 'mixed' -and @($selectedPause.process_ids).Count -eq 2) "(state=$($selectedPause.state))"
	$selectedContinue = Invoke-Tool -Name 'continue' -Arguments @{ session_id = $sessionId; process_id = $secondPid }
	Assert-That 'a selected process can continue independently' ($selectedContinue.state -eq 'running') "(state=$($selectedContinue.state))"
	$multiRestart = Invoke-Tool -Name 'restart' -Arguments @{ session_id = $sessionId } -ExpectError
	Assert-That 'restart is refused while multiple targets are active' ($multiRestart -match 'restart|launched through dgSpy')
	$afterSelectedTerminate = Invoke-Tool -Name 'terminate' -Arguments @{ session_id = $sessionId; process_id = $secondPid }
	Assert-That 'terminating one selected process leaves its sibling session active' ($afterSelectedTerminate.state -in @('running','paused') -and @($afterSelectedTerminate.process_ids).Count -eq 1 -and $afterSelectedTerminate.process_ids[0] -eq $targetId)
	Start-Sleep -Milliseconds 500
	Assert-That 'selected termination kills only the selected target' ($null -eq (Get-Process -Id $secondPid -ErrorAction SilentlyContinue) -and $null -ne (Get-Process -Id $targetId -ErrorAction SilentlyContinue))
	$selectedExit = @((Invoke-Tool -Name 'get_events' -Arguments @{ session_id = $sessionId; after_event_id = $beforeAmbiguous.last_event_id }).events | Where-Object { $_.process_id -eq $secondPid -and $_.kind -eq 'terminated' } | Select-Object -Last 1)
	Assert-That 'a selected target exit is non-terminal while a sibling remains' ($selectedExit.Count -eq 1 -and -not $selectedExit[0].terminal)

	$detachTargetOut = Join-Path $runDirectory 'detach-target.out'
	$detachTargetProcess = Start-Process -FilePath $targetExe -WindowStyle Hidden -PassThru `
		-RedirectStandardOutput $detachTargetOut -RedirectStandardError (Join-Path $runDirectory 'detach-target.err')
	if (-not (Wait-Until { @(Get-Content $detachTargetOut -ErrorAction SilentlyContinue).Count -ge 2 } 15)) {
		throw 'The selected-detach target did not publish its PID and method token.'
	}
	$detachPid = $detachTargetProcess.Id
	$detachProgram = @(Invoke-Tool -Name 'list_programs' -Arguments @{ process_ids = @($detachPid) })[0]
	$attachedSibling = Invoke-Tool -Name 'attach' -Arguments @{ program_id = $detachProgram.program_id }
	Assert-That 'attach adds an independently started process to the active session' (@($attachedSibling.process_ids).Count -eq 2 -and @($attachedSibling.process_ids) -contains $detachPid)
	$beforeSelectedDetach = Invoke-Tool -Name 'get_session_state' -Arguments @{ session_id = $sessionId }
	$selectedDetach = Invoke-Tool -Name 'detach' -Arguments @{ session_id = $sessionId; process_id = $detachPid }
	Assert-That 'selected detach removes only the attached process' ($selectedDetach.detached -and -not $selectedDetach.terminated -and $selectedDetach.process_id -eq $detachPid -and $selectedDetach.session_active)
	Start-Sleep -Milliseconds 500
	Assert-That 'selected detach leaves both external process and sibling alive' ($null -ne (Get-Process -Id $detachPid -ErrorAction SilentlyContinue) -and $null -ne (Get-Process -Id $targetId -ErrorAction SilentlyContinue))
	$selectedDetachEvent = @((Invoke-Tool -Name 'get_events' -Arguments @{ session_id = $sessionId; after_event_id = $beforeSelectedDetach.last_event_id }).events | Where-Object { $_.process_id -eq $detachPid -and $_.kind -eq 'detached' } | Select-Object -Last 1)
	Assert-That 'selected detach records a non-terminal detached event' ($selectedDetachEvent.Count -eq 1 -and -not $selectedDetachEvent[0].terminal -and $selectedDetachEvent[0].reason -eq 'detached_by_client')
	Stop-Process -Id $detachPid -Force

	$err = Invoke-Tool -Name 'get_session_state' -Arguments @{ session_id = 'not-a-session' } -ExpectError
	Assert-That 'an unknown session_id is refused' ($err -match 'not active|No active')

	$hostWithSession = Invoke-Tool -Name 'get_host_info' -Arguments @{}
	Assert-That 'get_host_info reports the live session' ($hostWithSession.session_id -eq $sessionId)
	$restartAttached = Invoke-Tool -Name 'restart' -Arguments @{ session_id = $sessionId } -ExpectError
	Assert-That 'restart refuses a target that dgSpy only attached to' ($restartAttached -match 'launched through dgSpy|restart support')

	Write-Host "== pause, inspect, resume ==" -ForegroundColor Cyan
	$paused = Invoke-Tool -Name 'pause' -Arguments @{ session_id = $sessionId }
	Assert-That 'pause reports paused, not the pre-pause state' ($paused.state -eq 'paused') "(was $($paused.state))"

	# Windows PowerShell's ConvertFrom-Json emits a JSON array as one Object[] pipeline item when it
	# leaves a function. Re-pipeline it here so selection receives individual thread records rather
	# than one nested array whose member access returns another array.
	$threadPayload = Invoke-Tool -Name 'list_threads' -Arguments @{ session_id = $sessionId }
	$threads = @($threadPayload | ForEach-Object { $_ })
	Assert-That 'list_threads reports target threads' ($threads.Count -gt 0)
	Assert-That 'thread_id values are unique' (@($threads.thread_id | Select-Object -Unique).Count -eq $threads.Count)
	$selectedThread = $threads | Where-Object { $_.is_current } | Select-Object -First 1
	if ($null -eq $selectedThread) { $selectedThread = $threads | Select-Object -First 1 }
	Assert-That 'list_threads identifies the current or first selectable thread' ($null -ne $selectedThread)
	Assert-That 'thread identity includes process and OS thread ids' ($selectedThread.thread_id -eq "$($selectedThread.process_id):$($selectedThread.os_thread_id)") "(was $($selectedThread.thread_id))"
	$badThread = Invoke-Tool -Name 'get_callstack' -Arguments @{ session_id = $sessionId; thread_id = 'not-a-thread' } -ExpectError
	Assert-That 'unknown caller-selected thread is refused' ($badThread -match 'not active|list_threads')

	$framePayload = Invoke-Tool -Name 'get_callstack' -Arguments @{ session_id = $sessionId; thread_id = $selectedThread.thread_id; max_frames = 10 }
	$frames = @($framePayload | ForEach-Object { $_ })
	Assert-That 'the call stack is populated right after attach+pause' ($frames.Count -gt 0)
	Assert-That 'caller-selected call stack stays on the requested thread' (@($frames | Where-Object { $_.thread_id -ne $selectedThread.thread_id }).Count -eq 0)
	$topFrame = Invoke-Tool -Name 'get_frame' -Arguments @{ session_id = $sessionId; thread_id = $selectedThread.thread_id; frame_index = 0 }
	Assert-That 'get_frame returns the caller-selected frame' ($topFrame.thread_id -eq $selectedThread.thread_id -and $topFrame.frame_index -eq 0 -and $topFrame.frame_id -eq $frames[0].frame_id)
	$badFrame = Invoke-Tool -Name 'get_frame' -Arguments @{ session_id = $sessionId; thread_id = $selectedThread.thread_id; frame_index = 99 } -ExpectError
	Assert-That 'unavailable caller-selected frame is refused' ($badFrame -match 'no frame|available frame_index')
	$tick = $frames | Where-Object { $_.method_token -eq $methodToken } | Select-Object -First 1
	Assert-That 'the stack contains the target method by token' ($null -ne $tick)
	if ($tick) {
		Assert-That 'the frame names the method, not just the module' ($tick.name -match 'Tick') "(was '$($tick.name)')"
		Assert-That 'the frame carries a module path usable for breakpoints' ($tick.module -like '*Milestone1Target.exe')
		$local = $tick.locals | Where-Object { $_.name -eq 'input' } | Select-Object -First 1
		Assert-That 'primitive locals are readable' ($null -ne $local -and $local.value -eq 41) "(input=$($local.value))"
	}

	Write-Host "== breakpoint and event cursor ==" -ForegroundColor Cyan
	$breakpoint = Invoke-Tool -Name 'set_il_breakpoint' -Arguments @{ session_id = $sessionId; module = $targetExe; method_token = $methodToken; il_offset = 0 }
	Assert-That 'set_il_breakpoint returns an id' ($null -ne $breakpoint.breakpoint_id)
	# The point of reporting binding state: an unbound breakpoint must not look like a bound one.
	Assert-That 'set_il_breakpoint reports the breakpoint as bound' ($breakpoint.bound -and $breakpoint.bound_count -ge 1) "(bound=$($breakpoint.bound) severity=$($breakpoint.severity) msg='$($breakpoint.message)')"
	Assert-That 'a bound breakpoint carries no error severity' ($breakpoint.severity -eq 'none') "(was $($breakpoint.severity))"
	Assert-That 'CorDebug accepts the offset without snapping' (-not $breakpoint.snapped)
	$listed = @(Invoke-Tool -Name 'list_breakpoints' -Arguments @{})
	Assert-That 'list_breakpoints reports the breakpoint' (@($listed | Where-Object { $_.breakpoint_id -eq $breakpoint.breakpoint_id }).Count -eq 1)
	$missingBreakpoint = Invoke-Tool -Name 'remove_breakpoint' -Arguments @{ breakpoint_id = 2147483647 } -ExpectError
	Assert-That 'remove_breakpoint refuses an unknown id' ($missingBreakpoint -match 'does not exist|list_breakpoints')
	$removed = Invoke-Tool -Name 'remove_breakpoint' -Arguments @{ breakpoint_id = $breakpoint.breakpoint_id }
	Assert-That 'remove_breakpoint reports the exact removed id' ($removed.removed -and $removed.breakpoint_id -eq $breakpoint.breakpoint_id)
	$afterRemove = @(Invoke-Tool -Name 'list_breakpoints' -Arguments @{})
	Assert-That 'remove_breakpoint leaves the removed breakpoint absent' (@($afterRemove | Where-Object { $_.breakpoint_id -eq $breakpoint.breakpoint_id }).Count -eq 0)
	$breakpoint = Invoke-Tool -Name 'set_il_breakpoint' -Arguments @{ session_id = $sessionId; module = $targetExe; method_token = $methodToken; il_offset = 0 }
	Assert-That 'a breakpoint can be recreated after targeted removal' ($breakpoint.bound)
	$cursor = (Invoke-Tool -Name 'get_session_state' -Arguments @{ session_id = $sessionId }).last_event_id
	Invoke-Tool -Name 'continue' -Arguments @{ session_id = $sessionId } | Out-Null
	$stop = Invoke-Tool -Name 'wait_for_stop' -Arguments @{ session_id = $sessionId; after_event_id = $cursor; timeout_ms = 8000 }
	Assert-That 'wait_for_stop observes the breakpoint hit' (-not $stop.timed_out -and @($stop.events).Count -gt 0)
	$firstStop = @($stop.events)[0]
	Assert-That 'the stop event is after the cursor' ($firstStop.event_id -gt $cursor)
	Assert-That 'the normalized stop preserves its reason and target identity' ($firstStop.stop_reason -eq 'breakpoint' -and $firstStop.process_id -eq $targetId -and -not [string]::IsNullOrWhiteSpace($firstStop.thread_id))
	Assert-That 'the normalized stop preserves breakpoint identity and IL location' ($firstStop.breakpoint_id -eq $breakpoint.breakpoint_id -and $firstStop.module -like '*Milestone1Target.exe' -and $firstStop.method_token -eq $methodToken -and $firstStop.il_offset -eq 0)
	$exactReason = Invoke-Tool -Name 'get_stop_reason' -Arguments @{ session_id = $sessionId; event_id = $firstStop.event_id }
	$latestReason = Invoke-Tool -Name 'get_stop_reason' -Arguments @{ session_id = $sessionId }
	Assert-That 'get_stop_reason returns an exact retained stop' ($exactReason.event_id -eq $firstStop.event_id -and $exactReason.stop_reason -eq 'breakpoint')
	Assert-That 'get_stop_reason returns the latest stop when event_id is omitted' ($latestReason.event_id -eq $firstStop.event_id)
	$filtered = Invoke-Tool -Name 'get_events' -Arguments @{ session_id = $sessionId; after_event_id = $cursor; kinds = @('stopped') }
	Assert-That 'get_events filters without consuming the stop' (@($filtered.events).Count -eq 1 -and @($filtered.events)[0].event_id -eq $firstStop.event_id)
	# step_completed is a real kind, and no step has been issued yet, so the wait times out on merit.
	# Do not use a made-up kind here: unknown kinds are now rejected outright, which is the point below.
	$timedOut = Invoke-Tool -Name 'wait_for_event' -Arguments @{ session_id = $sessionId; after_event_id = $firstStop.event_id; kinds = @('step_completed'); timeout_ms = 50 }
	Assert-That 'wait_for_event reports a bounded timeout' ($timedOut.timed_out -and @($timedOut.events).Count -eq 0)
	# A near miss must not look like "it never happened": that false negative is indistinguishable from a
	# breakpoint that does not fire, which is exactly the class of bug the event cursor already produced.
	$badKind = Invoke-Tool -Name 'wait_for_event' -Arguments @{ session_id = $sessionId; after_event_id = $cursor; kinds = @('breakpoint'); timeout_ms = 50 } -ExpectError
	Assert-That 'an unknown event kind is rejected instead of matching nothing' ($badKind -match 'Unknown event kind' -and $badKind -match 'breakpoint_hit')
	$badKindRead = Invoke-Tool -Name 'get_events' -Arguments @{ session_id = $sessionId; after_event_id = $cursor; kinds = @('stopped','nonsense') } -ExpectError
	Assert-That 'get_events rejects an unknown kind alongside a valid one' ($badKindRead -match 'nonsense')

	# Issue two long polls while the target is paused, then resume it. Both callers must receive the
	# same next stop; neither is allowed to consume the event or steal it from the other.
	$waitScript = {
		param($Url,$Token,$Session,$After)
		$body = @{ jsonrpc='2.0'; id=1; method='tools/call'; params=@{ name='wait_for_stop'; arguments=@{ session_id=$Session; after_event_id=$After; timeout_ms=8000 } } } | ConvertTo-Json -Depth 8
		(Invoke-RestMethod -Uri ($Url + '/mcp') -Method Post -ContentType 'application/json' -Headers @{ 'X-dgSpy-Token'=$Token } -TimeoutSec 15 -Body $body).result.content[0].text
	}
	$waiter1 = Start-Job -ScriptBlock $waitScript -ArgumentList $gatewayUrl,$token,$sessionId,$firstStop.event_id
	$waiter2 = Start-Job -ScriptBlock $waitScript -ArgumentList $gatewayUrl,$token,$sessionId,$firstStop.event_id
	Start-Sleep -Milliseconds 300
	$beforeResume = Invoke-Tool -Name 'get_session_state' -Arguments @{ session_id = $sessionId }
	Invoke-Tool -Name 'continue' -Arguments @{ session_id = $sessionId; expected_state_version = $beforeResume.state_version } | Out-Null
	$concurrent1 = (Receive-Job -Job $waiter1 -Wait -AutoRemoveJob) | ConvertFrom-Json
	$concurrent2 = (Receive-Job -Job $waiter2 -Wait -AutoRemoveJob) | ConvertFrom-Json
	$secondStop1 = @($concurrent1.events)[0]
	$secondStop2 = @($concurrent2.events)[0]
	Assert-That 'two waiters issued before resume observe the same breakpoint stop' (-not $concurrent1.timed_out -and -not $concurrent2.timed_out -and $secondStop1.event_id -eq $secondStop2.event_id)
	Assert-That 'resume then stop advances both event id and state version' ($secondStop1.event_id -gt $firstStop.event_id -and $secondStop1.state_version -gt $firstStop.state_version)

	Write-Host "== breakpoint settings ==" -ForegroundColor Cyan
	# The target is paused at the breakpoint here, which is the only state in which stepping is legal.
	$disabled = Invoke-Tool -Name 'update_breakpoint' -Arguments @{ breakpoint_id = $breakpoint.breakpoint_id; enabled = $false }
	Assert-That 'update_breakpoint disables a breakpoint' (-not $disabled.enabled)
	$conditioned = Invoke-Tool -Name 'update_breakpoint' -Arguments @{ breakpoint_id = $breakpoint.breakpoint_id; condition = 'input == 41'; hit_count = 3; hit_count_kind = 'multiple_of' }
	Assert-That 'update_breakpoint stores the condition' ($conditioned.condition -eq 'input == 41' -and $conditioned.condition_kind -eq 'is_true')
	Assert-That 'update_breakpoint stores the hit count and its kind' ($conditioned.hit_count -eq 3 -and $conditioned.hit_count_kind -eq 'multiple_of')
	Assert-That 'update_breakpoint leaves untouched fields alone' (-not $conditioned.enabled)
	$listedSettings = @(Invoke-Tool -Name 'list_breakpoints' -Arguments @{}) | Where-Object { $_.breakpoint_id -eq $breakpoint.breakpoint_id }
	Assert-That 'list_breakpoints reports the stored settings' ($listedSettings.condition -eq 'input == 41' -and $listedSettings.hit_count -eq 3)
	# A tracepoint that continues never stops, so wait_for_stop would wait forever on it. Saying so in a
	# warning is the difference between a documented behaviour and a hang the caller has to diagnose.
	$traced = Invoke-Tool -Name 'update_breakpoint' -Arguments @{ breakpoint_id = $breakpoint.breakpoint_id; trace_message = 'tick {input}'; trace_continue = $true }
	Assert-That 'update_breakpoint stores a tracepoint' ($traced.trace_message -eq 'tick {input}' -and $traced.trace_continue)
	Assert-That 'a continuing tracepoint warns that it produces no stop' ($traced.warning -match 'no stopped event|wait_for_stop')
	$cleared1 = Invoke-Tool -Name 'update_breakpoint' -Arguments @{ breakpoint_id = $breakpoint.breakpoint_id; condition = ''; trace_message = '' }
	Assert-That 'an empty string clears a condition rather than setting one' ($null -eq $cleared1.condition -and $null -eq $cleared1.trace_message)
	$badKindArg = Invoke-Tool -Name 'update_breakpoint' -Arguments @{ breakpoint_id = $breakpoint.breakpoint_id; condition = 'x'; condition_kind = 'if_true' } -ExpectError
	Assert-That 'an unknown condition_kind is rejected with the valid set' ($badKindArg -match 'when_changed')
	$noFields = Invoke-Tool -Name 'update_breakpoint' -Arguments @{ breakpoint_id = $breakpoint.breakpoint_id } -ExpectError
	Assert-That 'update_breakpoint refuses a no-op' ($noFields -match 'at least one')
	$missingBp = Invoke-Tool -Name 'update_breakpoint' -Arguments @{ breakpoint_id = 2147483647; enabled = $true } -ExpectError
	Assert-That 'update_breakpoint refuses an unknown id' ($missingBp -match 'does not exist')

	Write-Host "== stepping ==" -ForegroundColor Cyan
	# The breakpoint stays disabled across the step. Tick is hot enough that a re-arm would race the
	# step and stop for the breakpoint instead, which would pass for the wrong reason.
	$stepCursor = (Invoke-Tool -Name 'get_session_state' -Arguments @{ session_id = $sessionId }).last_event_id
	$stepped = Invoke-Tool -Name 'step_over' -Arguments @{ session_id = $sessionId; thread_id = $secondStop1.thread_id }
	Assert-That 'step_over reports the thread it stepped' ($stepped.thread_id -eq $secondStop1.thread_id) "(was $($stepped.thread_id))"
	Assert-That 'step_over reports its own kind' ($stepped.step_kind -eq 'over')
	Assert-That 'step_over returns a cursor taken before the step' ($stepped.cursor_event_id -ge $stepCursor)
	Assert-That 'step_over reports no engine error' ($null -eq $stepped.error) "(was $($stepped.error))"
	# Completion arrives on the event stream exactly like a breakpoint hit: same tool, same cursor
	# discipline, different stop_reason. That is the Phase 4 exit criterion.
	$stepStop = Invoke-Tool -Name 'wait_for_stop' -Arguments @{ session_id = $sessionId; after_event_id = $stepped.cursor_event_id; timeout_ms = 8000 }
	Assert-That 'the step completion arrives through wait_for_stop' (-not $stepStop.timed_out -and @($stepStop.events).Count -gt 0)
	$stepEvent = @($stepStop.events)[0]
	Assert-That 'the step stop is reported as a step, not a breakpoint' ($stepEvent.stop_reason -eq 'step') "(was $($stepEvent.stop_reason))"
	Assert-That 'the step stop names the stepped thread' ($stepEvent.thread_id -eq $secondStop1.thread_id)
	$stepEvents = Invoke-Tool -Name 'get_events' -Arguments @{ session_id = $sessionId; after_event_id = $stepped.cursor_event_id; kinds = @('step_completed') }
	Assert-That 'the raw step_completed event is recorded alongside the stop' (@($stepEvents.events).Count -ge 1)
	$badThreadStep = Invoke-Tool -Name 'step_into' -Arguments @{ session_id = $sessionId; thread_id = 'not-a-thread' } -ExpectError
	# The tool result carries the structured error's message, not its code. Match the message.
	Assert-That 'stepping an unknown thread is refused rather than guessed' ($badThreadStep -match 'is not active') "(was '$badThreadStep')"

	Write-Host "== exception breakpoints ==" -ForegroundColor Cyan
	$exception = Invoke-Tool -Name 'set_exception_breakpoint' -Arguments @{ name = 'System.InvalidOperationException'; stop_first_chance = $true }
	Assert-That 'set_exception_breakpoint reports what it set' ($exception.name -eq 'System.InvalidOperationException' -and $exception.stop_first_chance)
	Assert-That 'set_exception_breakpoint defaults to the DotNet category' ($exception.category -eq 'DotNet') "(was $($exception.category))"
	$exceptionList = Invoke-Tool -Name 'list_exception_breakpoints' -Arguments @{}
	Assert-That 'list_exception_breakpoints reports the configured entry' (@($exceptionList.entries | Where-Object { $_.name -eq 'System.InvalidOperationException' }).Count -eq 1)
	# The default listing is first-chance only, and that is the whole point: dnSpy stops on second chance
	# for essentially every .NET exception, so an unfiltered list is thousands of stock entries that are
	# identical on every machine. A first run of this check returned ~2500 of them.
	Assert-That 'the default listing is the deliberately configured set, not dnSpy stock defaults' (@($exceptionList.entries | Where-Object { -not $_.stop_first_chance }).Count -eq 0 -and $exceptionList.entries.Count -lt 50) "(got $($exceptionList.total))"
	Assert-That 'the default listing says it excluded second chance' (-not $exceptionList.included_second_chance)
	$stock = Invoke-Tool -Name 'list_exception_breakpoints' -Arguments @{ include_second_chance = $true; max_results = 10 }
	Assert-That 'including second chance exposes dnSpy stock defaults, bounded' ($stock.truncated -and $stock.total -gt 100 -and @($stock.entries).Count -eq 10) "(total=$($stock.total))"
	$exceptionOff = Invoke-Tool -Name 'set_exception_breakpoint' -Arguments @{ name = 'System.InvalidOperationException'; stop_first_chance = $false }
	Assert-That 'an exception breakpoint can be turned back off' (-not $exceptionOff.stop_first_chance)
	$afterOff = Invoke-Tool -Name 'list_exception_breakpoints' -Arguments @{}
	Assert-That 'a disabled exception entry leaves the first-chance list' (@($afterOff.entries | Where-Object { $_.name -eq 'System.InvalidOperationException' }).Count -eq 0)
	$badCategory = Invoke-Tool -Name 'set_exception_breakpoint' -Arguments @{ category = 'Klingon'; name = 'X'; stop_first_chance = $true } -ExpectError
	Assert-That 'an unknown exception category is refused with the known ones' ($badCategory -match 'DotNet')
	$noChange = Invoke-Tool -Name 'set_exception_breakpoint' -Arguments @{ name = 'System.Exception' } -ExpectError
	Assert-That 'set_exception_breakpoint refuses a no-op' ($noChange -match 'stop_first_chance')

	Write-Host "== evaluation ==" -ForegroundColor Cyan
	$stepThread = $secondStop1.thread_id
	$evaluated = Invoke-Tool -Name 'evaluate' -Arguments @{ session_id = $sessionId; expression = 'input'; thread_id = $stepThread; frame_index = 0 }
	Assert-That 'evaluate returns a raw scalar, not just display text' ($evaluated.has_raw_value -and $evaluated.value -eq 41) "(value=$($evaluated.value) display='$($evaluated.display)')"
	Assert-That 'evaluate reports the type separately from the value' ($evaluated.type -match 'int|Int32') "(was '$($evaluated.type)')"
	$arithmetic = Invoke-Tool -Name 'evaluate' -Arguments @{ session_id = $sessionId; expression = 'input + 1'; thread_id = $stepThread; frame_index = 0 }
	Assert-That 'evaluate computes an expression, not just a variable lookup' ($arithmetic.value -eq 42) "(was $($arithmetic.value))"
	$badExpression = Invoke-Tool -Name 'evaluate' -Arguments @{ session_id = $sessionId; expression = 'no_such_local'; thread_id = $stepThread; frame_index = 0 }
	Assert-That 'a bad expression reports an error rather than a fabricated value' (-not [string]::IsNullOrWhiteSpace($badExpression.error) -and -not $badExpression.has_raw_value)
	# Tick is static, so `this` genuinely does not exist here. The right answer is an error, not a
	# fabricated value — and it exercises the same path a caller hits by asking for the wrong thing.
	$thisValue = Invoke-Tool -Name 'evaluate' -Arguments @{ session_id = $sessionId; expression = 'this'; thread_id = $stepThread; frame_index = 0 }
	Assert-That 'this in a static method reports an error rather than a fabricated value' (-not [string]::IsNullOrWhiteSpace($thisValue.error) -and -not $thisValue.has_raw_value) "(error='$($thisValue.error)')"
	$label = Invoke-Tool -Name 'evaluate' -Arguments @{ session_id = $sessionId; expression = 'label'; thread_id = $stepThread; frame_index = 0 }
	Assert-That 'a reference local evaluates without a raw scalar or an error' ($null -eq $label.error) "(error='$($label.error)')"
	# CorDebug cannot create an object ID for every reference kind (notably strings). Main's commandLine
	# array is a stable heap object and exercises the actual persistent-reference path.
	$objectId = Invoke-Tool -Name 'create_object_id' -Arguments @{ session_id = $sessionId; expression = 'commandLine'; process_id = $targetId; runtime_id = $program.runtime_guid; thread_id = $stepThread; frame_index = 1 }
	Assert-That 'create_object_id returns runtime-scoped identity' ($objectId.object_id -ge 1 -and $objectId.process_id -eq $targetId)
	$listedIds = @(Invoke-Tool -Name 'list_object_ids' -Arguments @{ session_id = $sessionId; process_id = $targetId; runtime_id = $objectId.runtime_id } | ForEach-Object { $_ })
	Assert-That 'list_object_ids finds the created id' (@($listedIds | Where-Object { $_.object_id -eq $objectId.object_id }).Count -eq 1)
	$readId = Invoke-Tool -Name 'evaluate_object_id' -Arguments @{ session_id = $sessionId; object_id = $objectId.object_id; process_id = $targetId; runtime_id = $objectId.runtime_id; thread_id = $stepThread; frame_index = 1 }
	Assert-That 'evaluate_object_id resolves the persistent value' ($readId.value.error -eq $null)
	# Resume to a fresh breakpoint stop before reading the ID again. A same-stop read only proves lookup;
	# this proves CorDebug kept the strong handle while the target ran.
	$null = Invoke-Tool -Name 'update_breakpoint' -Arguments @{ breakpoint_id = $breakpoint.breakpoint_id; enabled = $true }
	$idResumeCursor = (Invoke-Tool -Name 'get_session_state' -Arguments @{ session_id = $sessionId }).last_event_id
	$null = Invoke-Tool -Name 'continue' -Arguments @{ session_id = $sessionId }
	$idResumeStop = Invoke-Tool -Name 'wait_for_stop' -Arguments @{ session_id = $sessionId; after_event_id = $idResumeCursor; timeout_ms = 8000 }
	Assert-That 'the target resumes and stops again while an object ID is retained' (-not $idResumeStop.timed_out)
	$stepThread = @($idResumeStop.events)[0].thread_id
	$readAfterResume = Invoke-Tool -Name 'evaluate_object_id' -Arguments @{ session_id = $sessionId; object_id = $objectId.object_id; process_id = $targetId; runtime_id = $objectId.runtime_id; thread_id = $stepThread; frame_index = 1 }
	Assert-That 'an object ID survives target resume' ($readAfterResume.value.error -eq $null -and $readAfterResume.object_id -eq $objectId.object_id)
	$null = Invoke-Tool -Name 'update_breakpoint' -Arguments @{ breakpoint_id = $breakpoint.breakpoint_id; enabled = $false }
	$releasedId = Invoke-Tool -Name 'release_object_id' -Arguments @{ session_id = $sessionId; object_id = $objectId.object_id; process_id = $targetId; runtime_id = $objectId.runtime_id }
	Assert-That 'release_object_id releases exactly the selected id' ($releasedId.object_id -eq $objectId.object_id)
	$releasedRead = Invoke-Tool -Name 'evaluate_object_id' -Arguments @{ session_id = $sessionId; object_id = $objectId.object_id; process_id = $targetId; runtime_id = $objectId.runtime_id; thread_id = $stepThread; frame_index = 1 } -ExpectError
	Assert-That 'a released object ID cannot be evaluated' ($releasedRead -match 'not active')
	$autos = @(Invoke-Tool -Name 'get_autos' -Arguments @{ session_id = $sessionId; process_id = $targetId; runtime_id = $objectId.runtime_id; thread_id = $stepThread; frame_index = 0 } | ForEach-Object { $_ })
	Assert-That 'get_autos returns structured C# Autos entries' ($autos.Count -gt 0 -and @($autos | Where-Object { $null -eq $_.expression -or $null -eq $_.display }).Count -eq 0) "(count=$($autos.Count); entries=$(($autos | ConvertTo-Json -Compress -Depth 5)))"
	$valueExport = Invoke-Tool -Name 'get_value_export' -Arguments @{ session_id = $sessionId; expression = 'input'; process_id = $targetId; runtime_id = $objectId.runtime_id; thread_id = $stepThread; frame_index = 0 }
	Assert-That 'get_value_export returns hashed bounded bytes' ($valueExport.total_size -eq 4 -and $valueExport.sha256.Length -eq 64 -and [Convert]::FromBase64String($valueExport.data_base64).Length -eq 4)
	$hostExport = Invoke-Tool -Name 'write_value_export' -Arguments @{ session_id = $sessionId; expression = 'input'; path = 'input.bin'; process_id = $targetId; runtime_id = $objectId.runtime_id; thread_id = $stepThread; frame_index = 0 }
	Assert-That 'write_value_export writes below the configured root with the same hash' ((Test-Path -LiteralPath $hostExport.path) -and $hostExport.sha256 -eq $valueExport.sha256 -and -not [string]::IsNullOrWhiteSpace($hostExport.audit_id))
	$pathEscape = Invoke-Tool -Name 'write_value_export' -Arguments @{ session_id = $sessionId; expression = 'input'; path = '..\outside.bin'; process_id = $targetId; runtime_id = $objectId.runtime_id; thread_id = $stepThread; frame_index = 0 } -ExpectError
	Assert-That 'write_value_export rejects traversal outside the configured root' ($pathEscape -match 'outside DGSPY_EXPORT_ROOT')
	$overwrite = Invoke-Tool -Name 'write_value_export' -Arguments @{ session_id = $sessionId; expression = 'input'; path = 'input.bin'; process_id = $targetId; runtime_id = $objectId.runtime_id; thread_id = $stepThread; frame_index = 0 } -ExpectError
	Assert-That 'write_value_export rejects overwrite by default' ($overwrite -match 'already exists')
	$liveExceptionPolicy = Invoke-Tool -Name 'set_exception_policy' -Arguments @{ session_id = $sessionId; category = 'DotNet'; name = 'Milestone1Target.Phase8FixtureException'; stop_thrown = $true; conditions = @(@{ kind = 'module_equals'; module = 'Milestone1Target.exe' }) }
	$null = Invoke-Tool -Name 'set_value' -Arguments @{ session_id = $sessionId; expression = 'throwPhase8Exception'; value = 'true'; thread_id = $stepThread; frame_index = 0 }
	$exceptionCursor = (Invoke-Tool -Name 'get_session_state' -Arguments @{ session_id = $sessionId }).last_event_id
	$null = Invoke-Tool -Name 'continue' -Arguments @{ session_id = $sessionId }
	$liveExceptionStop = Invoke-Tool -Name 'wait_for_stop' -Arguments @{ session_id = $sessionId; after_event_id = $exceptionCursor; timeout_ms = 8000 }
	$exceptionEvent = @($liveExceptionStop.events)[0]
	Assert-That 'a categorized thrown-exception policy causes an actual stop' (-not $liveExceptionStop.timed_out -and $exceptionEvent.stop_reason -eq 'exception') "(reason=$($exceptionEvent.stop_reason))"
	$exceptionValues = Invoke-Tool -Name 'get_exception' -Arguments @{ session_id = $sessionId; thread_id = $exceptionEvent.thread_id; frame_index = 0 }
	Assert-That 'get_exception exposes the stopped Phase 8 fixture exception' (@($exceptionValues).Count -gt 0 -and (($exceptionValues | ConvertTo-Json -Compress -Depth 5) -match 'Phase8FixtureException'))
	$null = Invoke-Tool -Name 'remove_exception_policy' -Arguments @{ session_id = $sessionId; category = 'DotNet'; name = 'Milestone1Target.Phase8FixtureException' }
	# The earlier settings checks intentionally left a hit-count rule on this breakpoint. Recreate it
	# here so this recovery assertion tests exception-policy removal, not residual breakpoint settings.
	$null = Invoke-Tool -Name 'remove_breakpoint' -Arguments @{ breakpoint_id = $breakpoint.breakpoint_id }
	$breakpoint = Invoke-Tool -Name 'set_il_breakpoint' -Arguments @{ session_id = $sessionId; module = $targetExe; method_token = $methodToken; il_offset = 0 }
	$returnCursor = (Invoke-Tool -Name 'get_session_state' -Arguments @{ session_id = $sessionId }).last_event_id
	$null = Invoke-Tool -Name 'continue' -Arguments @{ session_id = $sessionId }
	$returnStop = Invoke-Tool -Name 'wait_for_stop' -Arguments @{ session_id = $sessionId; after_event_id = $returnCursor; timeout_ms = 8000 }
	Assert-That 'the target resumes from the exception into the normal fixture breakpoint' (-not $returnStop.timed_out)
	$stepThread = @($returnStop.events)[0].thread_id
	$null = Invoke-Tool -Name 'update_breakpoint' -Arguments @{ breakpoint_id = $breakpoint.breakpoint_id; enabled = $false }

	$frameValues = Invoke-Tool -Name 'get_frame' -Arguments @{ session_id = $sessionId; thread_id = $stepThread; frame_index = 0; include = @('locals') }
	Assert-That 'get_frame include returns the full value list, objects included' (@($frameValues.values).Count -ge 1) "(got $(@($frameValues.values).Count))"
	Assert-That 'get_frame still reports primitive locals in locals' (@($frameValues.locals).Count -ge 1)
	$badInclude = Invoke-Tool -Name 'get_frame' -Arguments @{ session_id = $sessionId; thread_id = $stepThread; frame_index = 0; include = @('arguments') } -ExpectError
	Assert-That 'an unknown include value is rejected with the valid set' ($badInclude -match 'locals')

	# Depth is the caller's to control: a member's own expression is what goes back into get_members.
	# Frame 1 is Main, whose commandLine is a reference type that exists regardless of what Tick has run.
	$members = Invoke-Tool -Name 'get_members' -Arguments @{ session_id = $sessionId; expression = 'commandLine'; thread_id = $stepThread; frame_index = 1; count = 5 }
	Assert-That 'get_members expands a reference one level' ($members.total -ge 0 -and $members.expression -eq 'commandLine')
	Assert-That 'get_members reports paging state' ($members.offset -eq 0 -and -not $members.truncated)
	Assert-That 'every returned member carries the expression that reaches it again' (@($members.members | Where-Object { [string]::IsNullOrWhiteSpace($_.expression) }).Count -eq 0)
	# A primitive has nothing to expand. Zero members is the correct answer, not an error.
	$noMembers = Invoke-Tool -Name 'get_members' -Arguments @{ session_id = $sessionId; expression = 'input'; thread_id = $stepThread; frame_index = 0 }
	Assert-That 'expanding a primitive yields zero members rather than an error' ($noMembers.total -eq 0 -and @($noMembers.members).Count -eq 0)
	$memberFail = Invoke-Tool -Name 'get_members' -Arguments @{ session_id = $sessionId; expression = 'no_such_local'; thread_id = $stepThread; frame_index = 0 } -ExpectError
	Assert-That 'expanding a bad expression fails loudly' ($memberFail.Length -gt 0)

	# set_value executes in the target, so the read-back is the proof it took effect.
	$assigned = Invoke-Tool -Name 'set_value' -Arguments @{ session_id = $sessionId; expression = 'input'; value = '99'; thread_id = $stepThread; frame_index = 0 }
	Assert-That 'set_value assigns a local in the target' ($assigned.assigned) "(error='$($assigned.error)')"
	Assert-That 'set_value reads the new value back' ($assigned.value.value -eq 99) "(was $($assigned.value.value))"
	$badAssign = Invoke-Tool -Name 'set_value' -Arguments @{ session_id = $sessionId; expression = 'input'; value = '"not an int"'; thread_id = $stepThread; frame_index = 0 }
	Assert-That 'a type-mismatched assignment reports a compiler error and runs nothing' (-not $badAssign.assigned -and $badAssign.compiler_error) "(error='$($badAssign.error)' compiler=$($badAssign.compiler_error))"

	$noException = Invoke-Tool -Name 'get_exception' -Arguments @{ session_id = $sessionId; thread_id = $stepThread; frame_index = 0 } -AsText
	Assert-That 'get_exception returns empty at a non-exception stop' ($noException -eq '[]') "(payload $noException)"

	$watch = Invoke-Tool -Name 'add_watch' -Arguments @{ session_id = $sessionId; expression = 'input' }
	Assert-That 'add_watch returns an id' ($watch.watch_id -ge 1)
	$sameWatch = Invoke-Tool -Name 'add_watch' -Arguments @{ session_id = $sessionId; expression = 'input' }
	Assert-That 'adding the same expression twice reuses the id' ($sameWatch.watch_id -eq $watch.watch_id)
	$brokenWatch = Invoke-Tool -Name 'add_watch' -Arguments @{ session_id = $sessionId; expression = 'no_such_local' }
	# @(...) around a ConvertFrom-Json array can yield a one-element array holding the collection in
	# Windows PowerShell 5.1. Piping through ForEach-Object is the idiom that reliably flattens it.
	$watchList = @(Invoke-Tool -Name 'list_watches' -Arguments @{ session_id = $sessionId; thread_id = $stepThread; frame_index = 0 } | ForEach-Object { $_ })
	# Select into a variable before reaching through two levels. Chaining .value.error across a filtered
	# collection reads as fine and silently yields nothing in Windows PowerShell 5.1.
	$goodWatch = $watchList | Where-Object { $_.watch_id -eq $watch.watch_id } | Select-Object -First 1
	$failedWatch = $watchList | Where-Object { $_.watch_id -eq $brokenWatch.watch_id } | Select-Object -First 1
	Assert-That 'list_watches re-evaluates stored expressions' ($goodWatch.value.value -eq 99) "(was $($goodWatch.value.value))"
	# One bad expression must not hide the rest: that is the whole reason watches are evaluated in a
	# single pass with per-watch errors instead of failing the call.
	Assert-That 'a failing watch reports its own error without failing the call' ($watchList.Count -eq 2 -and -not [string]::IsNullOrWhiteSpace($failedWatch.value.error)) "(error='$($failedWatch.value.error)')"
	$removedWatch = Invoke-Tool -Name 'remove_watch' -Arguments @{ session_id = $sessionId; watch_id = $brokenWatch.watch_id }
	Assert-That 'remove_watch removes exactly one watch' ($removedWatch.removed -and $removedWatch.watch_id -eq $brokenWatch.watch_id)
	$missingWatch = Invoke-Tool -Name 'remove_watch' -Arguments @{ session_id = $sessionId; watch_id = 999999 } -ExpectError
	Assert-That 'remove_watch refuses an unknown id' ($missingWatch -match 'does not exist')

	# ForEach-Object, not @(...) alone: @() around a ConvertFrom-Json array yields a one-element array
	# holding the whole collection, and every filter below then matches that one item and passes on the
	# strength of some other module's flags. These two checks were green that way until the fixture
	# grew a module whose can_set_breakpoint is legitimately false.
	$modules = @(Invoke-Tool -Name 'list_modules' -Arguments @{ session_id = $sessionId } | ForEach-Object { $_ })
	Assert-That 'list_modules finds the target module' (@($modules | Where-Object { $_.filename -like '*Milestone1Target.exe' }).Count -eq 1) "(got $($modules.Count) modules)"
	Assert-That 'a file-backed module reports that it can carry a breakpoint' (@($modules | Where-Object { $_.filename -like '*Milestone1Target.exe' }).can_set_breakpoint)
	# Not "has no filename": an in-memory module reports a bare assembly name there, which is not a path
	# set_il_breakpoint can use. The dynamic and in-memory flags are the honest test.
	Assert-That 'every dynamic or in-memory module is marked as unable to carry a breakpoint' (@($modules | Where-Object { ($_.is_dynamic -or $_.is_in_memory) -and $_.can_set_breakpoint }).Count -eq 0)

	Write-Host "== advanced evaluation and low-level debugging ==" -ForegroundColor Cyan
	$invoked = Invoke-Tool -Name 'invoke_method' -Arguments @{ session_id = $sessionId; expression = 'System.Math.Abs(-7)'; thread_id = $stepThread; frame_index = 0; timeout_ms = 2000 }
	Assert-That 'invoke_method performs explicit audited func-eval' ($invoked.completed -and $invoked.causes_side_effects -and -not [string]::IsNullOrWhiteSpace($invoked.audit_id) -and $invoked.value.value -eq 7)
	$created = Invoke-Tool -Name 'create_object' -Arguments @{ session_id = $sessionId; expression = 'new System.Text.StringBuilder()'; thread_id = $stepThread; frame_index = 0; timeout_ms = 2000 }
	Assert-That 'create_object is a separate audited side-effect boundary' ($created.completed -and $created.causes_side_effects -and $created.capability -eq 'object_construction')
	$targetModule = $modules | Where-Object { $_.filename -like '*Milestone1Target.exe' } | Select-Object -First 1
	# Use the OS process value here. Windows PowerShell 5.1 turns a UInt64 read back through
	# ConvertFrom-Json into a PSCustomObject on some builds; that is a harness conversion artifact.
	$moduleAddress = [uint64](Get-Process -Id $targetId).MainModule.BaseAddress.ToInt64()
	$memory = Invoke-Tool -Name 'read_memory' -Arguments @{ session_id = $sessionId; process_id = $targetId; address = $moduleAddress; length = 2 }
	$memoryBytes = [Convert]::FromBase64String($memory.data_base64)
	Assert-That 'read_memory reads bounded target bytes' ($memoryBytes[0] -eq 0x4D -and $memoryBytes[1] -eq 0x5A -and -not $memory.causes_side_effects)
	$written = Invoke-Tool -Name 'write_memory' -Arguments @{ session_id = $sessionId; process_id = $targetId; address = $moduleAddress; data_base64 = $memory.data_base64 }
	Assert-That 'write_memory labels the idempotent fixture write as side effecting' ($written.written -and $written.causes_side_effects)
	$managedDisassembly = Invoke-Tool -Name 'get_disassembly' -Arguments @{ session_id = $sessionId; mode = 'managed'; module = $targetExe; method_token = $methodToken }
	Assert-That 'get_disassembly exposes managed IL with its capability' ($managedDisassembly.capability -eq 'managed_il' -and @($managedDisassembly.body.instructions).Count -gt 0)
	$nativeDisassembly = Invoke-Tool -Name 'get_disassembly' -Arguments @{ session_id = $sessionId; mode = 'native'; thread_id = $stepThread; frame_index = 0 }
	Assert-That 'get_disassembly exposes JIT native blocks when advertised' ($nativeDisassembly.capability -eq 'native_disassembly' -and @($nativeDisassembly.blocks).Count -gt 0)
	$registerFailure = Invoke-Tool -Name 'get_registers' -Arguments @{ session_id = $sessionId; thread_id = $stepThread } -ExpectError
	Assert-That 'get_registers returns a structured unsupported capability failure' ($registerFailure -match 'not exposed|unsupported')
	$currentFrame = Invoke-Tool -Name 'get_frame' -Arguments @{ session_id = $sessionId; thread_id = $stepThread; frame_index = 0 }
	$setIp = Invoke-Tool -Name 'set_instruction_pointer' -Arguments @{ session_id = $sessionId; thread_id = $stepThread; frame_index = 0; module = $currentFrame.module; method_token = $currentFrame.method_token; il_offset = $currentFrame.il_offset }
	Assert-That 'set_instruction_pointer validates and audits the selected frame' ($setIp.completed -and $setIp.causes_side_effects -and $setIp.capability -eq 'set_instruction_pointer')
	$outputMessages = Invoke-Tool -Name 'get_output' -Arguments @{ session_id = $sessionId; after_output_id = 0 }
	Assert-That 'get_output exposes bounded debugger messages including audits' (@($outputMessages.messages | Where-Object { $_.message -like '*dgSpy audit*' }).Count -ge 1)
	$moduleBreak = Invoke-Tool -Name 'set_module_breakpoint' -Arguments @{ session_id = $sessionId; module_name = 'DeferredPayload*'; is_loaded = $true }
	$moduleBreaks = @(Invoke-Tool -Name 'list_module_breakpoints' -Arguments @{ session_id = $sessionId } | ForEach-Object { $_ })
	Assert-That 'module breakpoints round-trip dnSpy filters' (@($moduleBreaks | Where-Object { $_.breakpoint_id -eq $moduleBreak.breakpoint_id -and $_.is_loaded }).Count -eq 1)
	$moduleCursor = (Invoke-Tool -Name 'get_session_state' -Arguments @{ session_id = $sessionId }).last_event_id
	$null = Invoke-Tool -Name 'set_value' -Arguments @{ session_id = $sessionId; expression = 'loadDeferredModule'; value = 'true'; thread_id = $stepThread; frame_index = 0 }
	$null = Invoke-Tool -Name 'continue' -Arguments @{ session_id = $sessionId }
	$moduleStop = Invoke-Tool -Name 'wait_for_stop' -Arguments @{ session_id = $sessionId; after_event_id = $moduleCursor; timeout_ms = 8000 }
	Assert-That 'a module-load breakpoint stops on an actual deferred Assembly.Load' (-not $moduleStop.timed_out -and @($moduleStop.events).Count -gt 0)
	$stepThread = @($moduleStop.events)[0].thread_id
	$loadedModules = @(Invoke-Tool -Name 'list_modules' -Arguments @{ session_id = $sessionId } | ForEach-Object { $_ })
	Assert-That 'the deferred in-memory module is visible after the module breakpoint' (@($loadedModules | Where-Object { $_.name -like 'DeferredPayload*' }).Count -eq 1)
	$null = Invoke-Tool -Name 'remove_module_breakpoint' -Arguments @{ session_id = $sessionId; breakpoint_id = $moduleBreak.breakpoint_id }
	$breakpointDocument = Invoke-Tool -Name 'export_breakpoints' -Arguments @{ session_id = $sessionId }
	$breakpointDryRun = Invoke-Tool -Name 'import_breakpoints' -Arguments @{ session_id = $sessionId; document = $breakpointDocument; mode = 'merge'; dry_run = $true }
	Assert-That 'breakpoint interchange dry-run validates without mutation' ($breakpointDryRun.dry_run -and $breakpointDryRun.removed -eq 0)
	$truncatedDocument = $breakpointDocument | ConvertTo-Json -Depth 20 | ConvertFrom-Json
	$truncatedDocument.exception_truncated = $true
	$truncatedReplace = Invoke-Tool -Name 'import_breakpoints' -Arguments @{ session_id = $sessionId; document = $truncatedDocument; mode = 'replace'; dry_run = $true } -ExpectError
	Assert-That 'replace rejects a truncated breakpoint document' ($truncatedReplace -match 'truncated')
	$sentinelModule = Invoke-Tool -Name 'set_module_breakpoint' -Arguments @{ session_id = $sessionId; module_name = 'Phase8.Replace.Sentinel'; is_loaded = $true }
	$null = Invoke-Tool -Name 'update_breakpoint' -Arguments @{ breakpoint_id = $breakpoint.breakpoint_id; enabled = $true; condition = 'input == -1' }
	$replaceDocument = $breakpointDocument | ConvertTo-Json -Depth 20 | ConvertFrom-Json
	$replaceDocument.exceptions = @(); $replaceDocument.exception_total = 0; $replaceDocument.exception_truncated = $false
	$replaceResult = Invoke-Tool -Name 'import_breakpoints' -Arguments @{ session_id = $sessionId; document = $replaceDocument; mode = 'replace' }
	$afterReplaceModules = @(Invoke-Tool -Name 'list_module_breakpoints' -Arguments @{ session_id = $sessionId } | ForEach-Object { $_ })
	$afterReplaceCode = @(Invoke-Tool -Name 'list_breakpoints' -Arguments @{} | ForEach-Object { $_ }) | Where-Object { $_.method_token -eq $breakpoint.method_token -and $_.il_offset -eq $breakpoint.il_offset }
	Assert-That 'replace removes breakpoints absent from the document' (@($afterReplaceModules | Where-Object { $_.breakpoint_id -eq $sentinelModule.breakpoint_id }).Count -eq 0 -and $replaceResult.removed -ge 1)
	Assert-That 'replace restores settings on a breakpoint with the same stable identity' (@($afterReplaceCode).Count -eq 1 -and -not $afterReplaceCode.enabled -and $null -eq $afterReplaceCode.condition) "(entries=$(($afterReplaceCode | ConvertTo-Json -Compress -Depth 5)))"
	$categories = @(Invoke-Tool -Name 'list_exception_categories' -Arguments @{ session_id = $sessionId } | ForEach-Object { $_ })
	Assert-That 'exception categories expose DotNet' (@($categories | Where-Object { $_.category -eq 'DotNet' }).Count -eq 1)
	$policy = Invoke-Tool -Name 'set_exception_policy' -Arguments @{ session_id = $sessionId; category = 'DotNet'; name = 'Milestone1Target.Phase8FixtureException'; stop_thrown = $true; stop_unhandled = $false; conditions = @(@{ kind = 'module_equals'; module = 'Milestone1Target.exe' }) }
	Assert-That 'exception policy mutation preserves flags and module conditions' ($policy.stop_thrown -and -not $policy.stop_unhandled -and @($policy.conditions).Count -eq 1 -and $policy.conditions[0].module -eq 'Milestone1Target.exe')
	$removedPolicy = Invoke-Tool -Name 'remove_exception_policy' -Arguments @{ session_id = $sessionId; category = 'DotNet'; name = 'Milestone1Target.Phase8FixtureException' }
	Assert-That 'remove_exception_policy returns the policy it removed' ($removedPolicy.name -eq 'Milestone1Target.Phase8FixtureException')
	$null = Invoke-Tool -Name 'set_exception_policy' -Arguments @{ session_id = $sessionId; category = 'DotNet'; name = 'Milestone1Target.Phase8FixtureException'; stop_thrown = $true }
	$null = Invoke-Tool -Name 'restore_exception_defaults' -Arguments @{ session_id = $sessionId }
	$removedAfterReset = Invoke-Tool -Name 'remove_exception_policy' -Arguments @{ session_id = $sessionId; category = 'DotNet'; name = 'Milestone1Target.Phase8FixtureException' } -ExpectError
	Assert-That 'restore_exception_defaults removes custom policies' ($removedAfterReset -match 'Phase8FixtureException') "(error='$removedAfterReset')"

	Write-Host "== symbols, IL and decompilation ==" -ForegroundColor Cyan
	$documents = @(Invoke-Tool -Name 'list_documents' -Arguments @{ session_id = $sessionId } | ForEach-Object { $_ })
	$targetDoc = $documents | Where-Object { $_.filename -like '*Milestone1Target.exe' } | Select-Object -First 1
	Assert-That 'list_documents finds the target and loads its metadata' ($null -ne $targetDoc -and $targetDoc.has_metadata) "(has_metadata=$($targetDoc.has_metadata))"
	Assert-That 'list_documents reports assembly identity and a type count' (-not [string]::IsNullOrWhiteSpace($targetDoc.assembly_full_name) -and $targetDoc.type_count -ge 1)

	$types = Invoke-Tool -Name 'list_types' -Arguments @{ session_id = $sessionId; module = $targetExe; name_pattern = 'Program' }
	Assert-That 'list_types finds a type by pattern' (@($types.symbols | Where-Object { $_.full_name -eq 'Milestone1Target.Program' }).Count -eq 1) "(total=$($types.total))"
	$members = Invoke-Tool -Name 'list_members' -Arguments @{ session_id = $sessionId; module = $targetExe; type = 'Milestone1Target.Program'; name_pattern = 'Tick' }
	$tickMember = $members.symbols | Where-Object { $_.name -eq 'Tick' } | Select-Object -First 1
	# The whole point of the symbol layer: a name in, the exact token set_il_breakpoint takes out.
	Assert-That 'list_members returns the metadata token the breakpoint tools take' ($tickMember.method_token -eq $methodToken) "(was $($tickMember.method_token), expected $methodToken)"
	Assert-That 'a member reports its declaring type and kind' ($tickMember.kind -eq 'method' -and $tickMember.declaring_type -eq 'Milestone1Target.Program')

	$search = Invoke-Tool -Name 'search_symbols' -Arguments @{ session_id = $sessionId; pattern = 'Tick'; module = 'Milestone1Target'; kinds = @('method') }
	Assert-That 'search_symbols reaches a method by name across modules' (@($search.symbols | Where-Object { $_.method_token -eq $methodToken }).Count -eq 1) "(total=$($search.total))"
	$bounded = Invoke-Tool -Name 'search_symbols' -Arguments @{ session_id = $sessionId; pattern = 'e'; module = 'Milestone1Target'; count = 3 }
	Assert-That 'search_symbols is bounded and reports truncation' (@($bounded.symbols).Count -le 3 -and $bounded.total -ge @($bounded.symbols).Count)

	$il = Invoke-Tool -Name 'get_il' -Arguments @{ session_id = $sessionId; module = $targetExe; method_token = $methodToken }
	Assert-That 'get_il disassembles the method by token' (@($il.instructions).Count -gt 0 -and $il.method_token -eq $methodToken)
	Assert-That 'get_il names the method and its declaring type' ($il.full_name -match 'Tick' -and $il.declaring_type -eq 'Milestone1Target.Program')
	# This is what makes Mono's sequence-point rule discoverable instead of trial and error.
	Assert-That 'get_il marks which offsets a Mono breakpoint could bind at' ($il.has_sequence_points -and @($il.instructions | Where-Object { $_.is_sequence_point }).Count -ge 1)
	$ilByName = Invoke-Tool -Name 'get_il' -Arguments @{ session_id = $sessionId; module = $targetExe; type = 'Milestone1Target.Program'; method = 'Tick' }
	Assert-That 'get_il resolves the same method by name as by token' ($ilByName.method_token -eq $methodToken)
	$missingMethod = Invoke-Tool -Name 'get_il' -Arguments @{ session_id = $sessionId; module = $targetExe; type = 'Milestone1Target.Program'; method = 'NoSuchMethod' } -ExpectError
	Assert-That 'an unknown method is refused with a pointer to list_members' ($missingMethod -match 'list_members')

	$csharp = Invoke-Tool -Name 'get_csharp' -Arguments @{ session_id = $sessionId; module = $targetExe; method_token = $methodToken }
	Assert-That 'get_csharp decompiles the method body' ($csharp.code -match 'Tick' -and $csharp.code -match 'Sleep') "(len=$($csharp.code.Length))"
	Assert-That 'get_csharp names the language it used' ($csharp.language -match 'C#')

	$text = Invoke-Tool -Name 'search_text' -Arguments @{ session_id = $sessionId; module = 'Milestone1Target'; type = 'Milestone1Target.Program'; pattern = 'phase-six-text-search-fixture'; count = 10; max_methods = 20 }
	Assert-That 'search_text finds decompiled method text with token identity' (@($text.hits | Where-Object { $_.method -match 'UseWorker' -and $_.method_token -gt 0 }).Count -eq 1) "(total=$($text.total))"
	$boundedText = Invoke-Tool -Name 'search_text' -Arguments @{ session_id = $sessionId; module = 'Milestone1Target'; pattern = 'not-present'; max_methods = 1 }
	Assert-That 'search_text bounds work as well as output' ($boundedText.scanned_methods -eq 1 -and $boundedText.scan_truncated)

	$workerMembers = Invoke-Tool -Name 'list_members' -Arguments @{ session_id = $sessionId; module = $targetExe; type = 'Milestone1Target.IWorker'; name_pattern = 'Run' }
	$runToken = @($workerMembers.symbols | Where-Object { $_.kind -eq 'method' })[0].method_token
	$refs = Invoke-Tool -Name 'find_references' -Arguments @{ session_id = $sessionId; module = $targetExe; token = $runToken; search_module = 'Milestone1Target'; count = 10 }
	Assert-That 'find_references identifies the containing method by token' (@($refs.symbols | Where-Object { $_.name -eq 'UseWorker' }).Count -eq 1) "(total=$($refs.total))"
	$analysis = Invoke-Tool -Name 'analyze_symbol' -Arguments @{ session_id = $sessionId; module = $targetExe; token = $runToken; search_module = 'Milestone1Target'; count = 20 }
	Assert-That 'analyze_symbol returns typed endpoint identities' (@($analysis.edges | Where-Object { $_.kind -eq 'caller' -and $_.source.name -eq 'UseWorker' -and $_.target.method_token -eq $runToken }).Count -eq 1) "(total=$($analysis.total))"
	$useWorker = @(Invoke-Tool -Name 'list_members' -Arguments @{ session_id = $sessionId; module = $targetExe; type = 'Milestone1Target.Program'; name_pattern = 'UseWorker' }).symbols | Select-Object -First 1
	$calleeAnalysis = Invoke-Tool -Name 'analyze_symbol' -Arguments @{ session_id = $sessionId; module = $targetExe; token = $useWorker.method_token; search_module = 'Milestone1Target'; count = 20 }
	Assert-That 'analyze_symbol returns typed callee edges' (@($calleeAnalysis.edges | Where-Object { $_.kind -eq 'callee' -and $_.target.name -eq 'Run' }).Count -ge 1)
	$boundedAnalysis = Invoke-Tool -Name 'analyze_symbol' -Arguments @{ session_id = $sessionId; module = $targetExe; token = $runToken; search_module = 'Milestone1Target'; count = 20; max_methods = 1 }
	Assert-That 'analyze_symbol enforces its hard method scan budget' ($boundedAnalysis.scanned_methods -eq 1 -and $boundedAnalysis.scan_truncated -and $boundedAnalysis.truncated)
	$constructors = @(Invoke-Tool -Name 'list_members' -Arguments @{ session_id = $sessionId; module = $targetExe; type = 'Milestone1Target.Worker'; name_pattern = '.ctor' }).symbols
	$constructorAnalysis = Invoke-Tool -Name 'analyze_symbol' -Arguments @{ session_id = $sessionId; module = $targetExe; token = $constructors[0].method_token; search_module = 'Milestone1Target'; count = 20 }
	Assert-That 'analyze_symbol identifies construction sites' (@($constructorAnalysis.edges | Where-Object { $_.kind -eq 'constructs' -and $_.source.name -eq 'ExercisePhase8Relationships' }).Count -eq 1)
	$field = @(Invoke-Tool -Name 'list_members' -Arguments @{ session_id = $sessionId; module = $targetExe; type = 'Milestone1Target.Program'; name_pattern = 'observedWorkerValue' }).symbols | Select-Object -First 1
	$fieldAnalysis = Invoke-Tool -Name 'analyze_symbol' -Arguments @{ session_id = $sessionId; module = $targetExe; token = $field.method_token; search_module = 'Milestone1Target'; count = 20 }
	Assert-That 'analyze_symbol distinguishes field reads and writes' (@($fieldAnalysis.edges | Where-Object { $_.kind -eq 'field_read' }).Count -ge 1 -and @($fieldAnalysis.edges | Where-Object { $_.kind -eq 'field_write' }).Count -ge 1)
	$event = @(Invoke-Tool -Name 'list_members' -Arguments @{ session_id = $sessionId; module = $targetExe; type = 'Milestone1Target.Program'; name_pattern = 'Phase8Event' }).symbols | Where-Object { $_.kind -eq 'event' } | Select-Object -First 1
	$eventAnalysis = Invoke-Tool -Name 'analyze_symbol' -Arguments @{ session_id = $sessionId; module = $targetExe; token = $event.method_token; search_module = 'Milestone1Target'; count = 20 }
	Assert-That 'analyze_symbol distinguishes event add and remove access' (@($eventAnalysis.edges | Where-Object { $_.kind -eq 'event_add' }).Count -ge 1 -and @($eventAnalysis.edges | Where-Object { $_.kind -eq 'event_remove' }).Count -ge 1)
	$marker = @(Invoke-Tool -Name 'list_types' -Arguments @{ session_id = $sessionId; module = $targetExe; name_pattern = 'Phase8MarkerAttribute' }).symbols | Select-Object -First 1
	$attributeAnalysis = Invoke-Tool -Name 'analyze_symbol' -Arguments @{ session_id = $sessionId; module = $targetExe; token = $marker.method_token; search_module = 'Milestone1Target'; count = 20 }
	Assert-That 'analyze_symbol identifies attribute relationships' (@($attributeAnalysis.edges | Where-Object { $_.kind -eq 'attribute' -and $_.source.full_name -eq 'Milestone1Target.Worker' }).Count -eq 1)
	$ifaceTypes = Invoke-Tool -Name 'list_types' -Arguments @{ session_id = $sessionId; module = $targetExe; name_pattern = 'IWorker' }
	$ifaceToken = @($ifaceTypes.symbols | Where-Object { $_.full_name -eq 'Milestone1Target.IWorker' })[0].method_token
	$interfaceAnalysis = Invoke-Tool -Name 'analyze_symbol' -Arguments @{ session_id = $sessionId; module = $targetExe; token = $ifaceToken; search_module = 'Milestone1Target'; count = 20 }
	Assert-That 'analyze_symbol identifies interface implementation relationships' (@($interfaceAnalysis.edges | Where-Object { $_.kind -eq 'implements' -and $_.source.full_name -eq 'Milestone1Target.Worker' }).Count -eq 1)
	$baseRun = @(Invoke-Tool -Name 'list_members' -Arguments @{ session_id = $sessionId; module = $targetExe; type = 'Milestone1Target.BaseWorker'; name_pattern = 'Run' }).symbols | Select-Object -First 1
	$overrideAnalysis = Invoke-Tool -Name 'analyze_symbol' -Arguments @{ session_id = $sessionId; module = $targetExe; token = $baseRun.method_token; search_module = 'Milestone1Target'; count = 20 }
	Assert-That 'analyze_symbol identifies ordinary virtual overrides' (@($overrideAnalysis.edges | Where-Object { $_.kind -eq 'override' -and $_.source.full_name -eq 'Milestone1Target.Worker.Run' }).Count -eq 1) "(edges=$(($overrideAnalysis.edges | ConvertTo-Json -Compress -Depth 5)))"
	$impls = Invoke-Tool -Name 'find_implementations' -Arguments @{ session_id = $sessionId; module = $targetExe; token = $ifaceToken; search_module = 'Milestone1Target'; count = 10 }
	Assert-That 'find_implementations finds a direct interface implementer' (@($impls.symbols | Where-Object { $_.full_name -eq 'Milestone1Target.Worker' }).Count -eq 1) "(total=$($impls.total))"

	$metadata = Invoke-Tool -Name 'get_metadata' -Arguments @{ session_id = $sessionId; module = $targetExe; token = $methodToken }
	Assert-That 'get_metadata reports table counts and resolves a token' ($metadata.table_row_counts.TypeDef -ge 4 -and $metadata.table_row_counts.MethodDef -gt 0 -and $metadata.token_full_name -match 'Tick')

	$raw = Invoke-Tool -Name 'get_raw_module' -Arguments @{ session_id = $sessionId; module = $targetExe; offset = 0; count = 64 }
	$rawBytes = [Convert]::FromBase64String($raw.data_base64)
	$fileHash = (Get-FileHash -Algorithm SHA256 $targetExe).Hash.ToLowerInvariant()
	Assert-That 'get_raw_module returns a bounded PE chunk' ($rawBytes.Length -eq 64 -and $rawBytes[0] -eq 0x4D -and $rawBytes[1] -eq 0x5A -and $raw.truncated)
	Assert-That 'get_raw_module hashes the complete image' ($raw.sha256 -eq $fileHash) "(rpc=$($raw.sha256) file=$fileHash)"

	# set_breakpoint by name must be the same breakpoint set_il_breakpoint produces, not a parallel path.
	Invoke-Tool -Name 'clear_breakpoints' -Arguments @{} | Out-Null
	$named = Invoke-Tool -Name 'set_breakpoint' -Arguments @{ session_id = $sessionId; module = 'Milestone1Target.exe'; type = 'Milestone1Target.Program'; method = 'Tick' }
	Assert-That 'set_breakpoint resolves a name to the right method token' ($named.method_token -eq $methodToken) "(was $($named.method_token))"
	Assert-That 'set_breakpoint binds exactly as the token-based tool does' ($named.bound -and $named.severity -eq 'none') "(bound=$($named.bound) msg='$($named.message)')"
	Assert-That 'set_breakpoint returns a cursor for wait_for_stop' ($null -ne $named.cursor_event_id)
	$ambiguous = Invoke-Tool -Name 'set_breakpoint' -Arguments @{ session_id = $sessionId; module = 'Milestone1Target.exe'; type = 'Milestone1Target.Program'; method = 'ReadInt' } -ExpectError:$false
	Assert-That 'a non-overloaded sibling method also resolves' ($ambiguous.method_token -gt 0)
	$badType = Invoke-Tool -Name 'set_breakpoint' -Arguments @{ session_id = $sessionId; module = 'Milestone1Target.exe'; type = 'Nope.Missing'; method = 'Tick' } -ExpectError
	Assert-That 'an unknown type is refused with a pointer to list_types' ($badType -match 'list_types')
	$badModule = Invoke-Tool -Name 'get_il' -Arguments @{ session_id = $sessionId; module = 'NotLoaded.dll'; method_token = $methodToken } -ExpectError
	Assert-That 'an unloaded module is refused with a pointer to list_modules' ($badModule -match 'list_modules')
	Invoke-Tool -Name 'clear_breakpoints' -Arguments @{} | Out-Null

	# The file-less module path was reasoned about rather than exercised: the only real specimen anyone
	# had seen was a frame on a live UCH stack. The fixture now carries two of its own — an assembly
	# loaded from bytes and a Reflection.Emit dynamic assembly — so the whole claim is testable here:
	# metadata resolves, breakpoints are refused explicitly, and a frame belonging to a module with no
	# path is still navigable to its IL. This covers CorDebug only; Mono is a different engine and still
	# needs the manual UCH pass.
	Write-Host "== dynamic and in-memory modules ==" -ForegroundColor Cyan
	$flDocs = @(Invoke-Tool -Name 'list_documents' -Arguments @{ session_id = $sessionId } | ForEach-Object { $_ })
	$flModules = @(Invoke-Tool -Name 'list_modules' -Arguments @{ session_id = $sessionId } | ForEach-Object { $_ })
	$flSeen = ($flDocs | Where-Object { $_.is_dynamic -or $_.is_in_memory } |
		ForEach-Object { "$($_.name)/'$($_.filename)'/'$($_.assembly_full_name)'/meta=$($_.has_metadata)" }) -join ', '
	Assert-That 'the fixture produces file-less modules at all' (@($flModules | Where-Object { $_.is_dynamic -or $_.is_in_memory }).Count -ge 2) "(file-less documents: $flSeen)"

	foreach ($flCase in @(
		@{ Label = 'an in-memory'; Assembly = 'InMemoryPayload'; Type = 'InMemoryPayload.Trampoline'; Callee = 'ViaInMemory'; Dynamic = $false },
		@{ Label = 'a dynamic'; Assembly = 'DgSpyDynamicPayload'; Type = 'DgSpyDynamicPayload.Trampoline'; Callee = 'ViaDynamic'; Dynamic = $true })) {

		# Identify by assembly identity from metadata, not by dnSpy's display name: the display name for
		# a module with no file is derived from its load address and differs every run.
		$flDoc = $flDocs | Where-Object { $_.assembly_full_name -like ($flCase.Assembly + ',*') } | Select-Object -First 1
		Assert-That "$($flCase.Label) module resolves its metadata through DbgMetadataService" ($null -ne $flDoc -and $flDoc.has_metadata) "(file-less documents: $flSeen)"
		if ($null -eq $flDoc) { continue }
		$flName = $flDoc.name
		# A file-less module does not necessarily report an empty filename: an in-memory one reports its
		# bare assembly name. What matters is that it is not a path anything can be loaded from.
		Assert-That "$($flCase.Label) module reports no usable file path" (-not [IO.Path]::IsPathRooted($flDoc.filename)) "(was '$($flDoc.filename)')"
		# dnSpy reports a dynamic module as in-memory as well — it has no file either way — so in-memory
		# is true for both and is_dynamic is what separates them.
		Assert-That "$($flCase.Label) module is classified in-memory, and dynamic only when it is" ($flDoc.is_in_memory -and $flDoc.is_dynamic -eq $flCase.Dynamic) "(is_dynamic=$($flDoc.is_dynamic) is_in_memory=$($flDoc.is_in_memory))"

		$flModule = $flModules | Where-Object { $_.name -eq $flName } | Select-Object -First 1
		Assert-That "list_modules lists $($flCase.Label) module under the name list_documents used" ($null -ne $flModule)
		Assert-That "list_modules flags $($flCase.Label) module as unable to carry a breakpoint" ($null -ne $flModule -and -not $flModule.can_set_breakpoint)

		# Everything below addresses the module by that name alone, which is all a caller holding a frame
		# or a search result has. A path would be the easy case and is not the one in question.
		$flMetadata = Invoke-Tool -Name 'get_metadata' -Arguments @{ session_id = $sessionId; module = $flName }
		Assert-That "get_metadata reads $($flCase.Label) module by name" ($flMetadata.assembly_full_name -like ($flCase.Assembly + ',*') -and $flMetadata.table_row_counts.MethodDef -ge 1) "(assembly='$($flMetadata.assembly_full_name)')"
		$flTypes = Invoke-Tool -Name 'list_types' -Arguments @{ session_id = $sessionId; module = $flName; name_pattern = 'Trampoline' }
		Assert-That "list_types finds the type inside $($flCase.Label) module" (@($flTypes.symbols | Where-Object { $_.full_name -eq $flCase.Type }).Count -eq 1) "(total=$($flTypes.total))"
		$flMembers = Invoke-Tool -Name 'list_members' -Arguments @{ session_id = $sessionId; module = $flName; type = $flCase.Type; name_pattern = 'Call' }
		$flCall = $flMembers.symbols | Where-Object { $_.name -eq 'Call' -and $_.kind -eq 'method' } | Select-Object -First 1
		Assert-That "list_members returns a metadata token from $($flCase.Label) module" ($null -ne $flCall -and $flCall.method_token -gt 0) "(token=$($flCall.method_token))"

		$flIl = Invoke-Tool -Name 'get_il' -Arguments @{ session_id = $sessionId; module = $flName; type = $flCase.Type; method = 'Call' }
		Assert-That "get_il disassembles a method in $($flCase.Label) module" (@($flIl.instructions).Count -gt 0 -and $flIl.method_token -eq $flCall.method_token)
		Assert-That "the IL from $($flCase.Label) module contains the callback it makes" (@($flIl.instructions | Where-Object { $_.opcode -match 'call' }).Count -ge 1)
		$flCsharp = Invoke-Tool -Name 'get_csharp' -Arguments @{ session_id = $sessionId; module = $flName; method_token = $flCall.method_token }
		Assert-That "get_csharp decompiles a method in $($flCase.Label) module" ($flCsharp.code -match 'Call') "(len=$($flCsharp.code.Length))"

		# A module with no file is served by serializing the runtime's own metadata back into an image,
		# so the hash is of that image and deliberately not of any file on disk.
		$flRaw = Invoke-Tool -Name 'get_raw_module' -Arguments @{ session_id = $sessionId; module = $flName; offset = 0; count = 64 }
		$flRawBytes = [Convert]::FromBase64String($flRaw.data_base64)
		Assert-That "get_raw_module serializes $($flCase.Label) module to a PE image" ($flRawBytes.Length -eq 64 -and $flRawBytes[0] -eq 0x4D -and $flRawBytes[1] -eq 0x5A -and $flRaw.total_size -gt 64 -and $flRaw.truncated) "(total=$($flRaw.total_size))"
		Assert-That "get_raw_module hashes the whole serialized image" ($flRaw.sha256 -match '^[0-9a-f]{64}$') "(was '$($flRaw.sha256)')"

		# The point of the whole flag: refused explicitly, with the reason, rather than accepted as a
		# breakpoint that silently never binds.
		$flRefused = Invoke-Tool -Name 'set_breakpoint' -Arguments @{ session_id = $sessionId; module = $flName; type = $flCase.Type; method = 'Call' } -ExpectError
		Assert-That "set_breakpoint refuses $($flCase.Label) module by name with the reason" ($flRefused -match 'in-memory or dynamic module') "(was '$flRefused')"
		# set_il_breakpoint has no such guard — it takes a path and gets a name. It must at least not
		# claim to be bound, which is what would make the gap invisible to a caller.
		$flUnbound = Invoke-Tool -Name 'set_il_breakpoint' -Arguments @{ session_id = $sessionId; module = $flName; method_token = $flCall.method_token; il_offset = 0 }
		Assert-That "set_il_breakpoint on $($flCase.Label) module does not report a bound breakpoint" (-not $flUnbound.bound) "(bound=$($flUnbound.bound) severity=$($flUnbound.severity))"
		Invoke-Tool -Name 'clear_breakpoints' -Arguments @{} | Out-Null

		# The original UCH observation was a *frame* naming a module with no file. Reproduce it: the
		# callee is reached only through this module, so frame 1 belongs to it.
		$flNamed = Invoke-Tool -Name 'set_breakpoint' -Arguments @{ session_id = $sessionId; module = 'Milestone1Target.exe'; type = 'Milestone1Target.Program'; method = $flCase.Callee }
		Assert-That "a breakpoint binds in the method $($flCase.Label) module calls" ($flNamed.bound) "(msg='$($flNamed.message)')"
		Invoke-Tool -Name 'continue' -Arguments @{ session_id = $sessionId } | Out-Null
		$flStop = Invoke-Tool -Name 'wait_for_stop' -Arguments @{ session_id = $sessionId; after_event_id = $flNamed.cursor_event_id; timeout_ms = 8000 }
		Assert-That "the call through $($flCase.Label) module reaches the breakpoint" (-not $flStop.timed_out -and @($flStop.events).Count -gt 0)
		if (-not $flStop.timed_out) {
			$flFrames = @(Invoke-Tool -Name 'get_callstack' -Arguments @{ session_id = $sessionId; thread_id = @($flStop.events)[0].thread_id; max_frames = 4 } | ForEach-Object { $_ })
			Assert-That "the stop is inside the method $($flCase.Label) module calls" ($flFrames[0].name -match $flCase.Callee) "(was '$($flFrames[0].name)')"
			# This is the deferred Phase 4 limitation, stated as an assertion: the caller frame names a
			# module and carries no path for it.
			Assert-That "the caller frame names $($flCase.Label) module and reports no path for it" ($flFrames[1].module_name -eq $flName -and -not [IO.Path]::IsPathRooted($flFrames[1].module)) "(module_name='$($flFrames[1].module_name)' module='$($flFrames[1].module)')"
			# Frame identity in, IL out, with nothing but what the frame itself reported.
			$flFrameIl = Invoke-Tool -Name 'get_il' -Arguments @{ session_id = $sessionId; module = $flFrames[1].module_name; method_token = $flFrames[1].method_token }
			Assert-That "that frame's own identity is enough to fetch its IL" ($flFrameIl.method_token -eq $flFrames[1].method_token -and $flFrameIl.declaring_type -eq $flCase.Type) "(declaring_type='$($flFrameIl.declaring_type)')"
		}
		Invoke-Tool -Name 'clear_breakpoints' -Arguments @{} | Out-Null
	}

	# Leave the session paused where the rest of the script found it: at Tick's entry. The later check
	# that a running target refuses get_callstack resumes and asks immediately, so it needs Tick's 100ms
	# body ahead of it — resuming from the trampolines instead lands on the next Tick hit in microseconds.
	$flSettle = Invoke-Tool -Name 'set_breakpoint' -Arguments @{ session_id = $sessionId; module = 'Milestone1Target.exe'; type = 'Milestone1Target.Program'; method = 'Tick' }
	Invoke-Tool -Name 'continue' -Arguments @{ session_id = $sessionId } | Out-Null
	$flSettled = Invoke-Tool -Name 'wait_for_stop' -Arguments @{ session_id = $sessionId; after_event_id = $flSettle.cursor_event_id; timeout_ms = 8000 }
	Assert-That 'the session returns to a stop inside the fixture loop' (-not $flSettled.timed_out)
	Invoke-Tool -Name 'clear_breakpoints' -Arguments @{} | Out-Null

	# Exercise unload only after inspecting the long-lived dynamic module. CorDebug invalidates metadata
	# for Reflection.Emit modules when an unrelated AppDomain unloads; that engine behavior should not
	# make the independent file-less-module checks order-dependent.
	$stepThread = @($flSettled.events)[0].thread_id
	$moduleUnload = Invoke-Tool -Name 'set_module_breakpoint' -Arguments @{ session_id = $sessionId; module_name = 'DeferredPayload*'; is_loaded = $false }
	$unloadCursor = (Invoke-Tool -Name 'get_session_state' -Arguments @{ session_id = $sessionId }).last_event_id
	$null = Invoke-Tool -Name 'set_value' -Arguments @{ session_id = $sessionId; expression = 'unloadDeferredModule'; value = 'true'; thread_id = $stepThread; frame_index = 0 }
	$null = Invoke-Tool -Name 'continue' -Arguments @{ session_id = $sessionId }
	$unloadStop = Invoke-Tool -Name 'wait_for_stop' -Arguments @{ session_id = $sessionId; after_event_id = $unloadCursor; timeout_ms = 8000 }
	Assert-That 'a module-unload breakpoint stops on AppDomain unload' (-not $unloadStop.timed_out -and @($unloadStop.events).Count -gt 0)
	$null = Invoke-Tool -Name 'remove_module_breakpoint' -Arguments @{ session_id = $sessionId; breakpoint_id = $moduleUnload.breakpoint_id }

	$breakpoint = Invoke-Tool -Name 'set_il_breakpoint' -Arguments @{ session_id = $sessionId; module = $targetExe; method_token = $methodToken; il_offset = 0 }

	$reenabled = Invoke-Tool -Name 'update_breakpoint' -Arguments @{ breakpoint_id = $breakpoint.breakpoint_id; enabled = $true }
	Assert-That 'the code breakpoint can be re-enabled after stepping' ($reenabled.enabled)

	$stale = Invoke-Tool -Name 'pause' -Arguments @{ session_id = $sessionId; expected_state_version = 1 } -ExpectError
	Assert-That 'a stale expected_state_version is rejected' ($stale -match 'stale|Expected state')

	# Evaluation runs off the dispatcher now, so a running target must be refused explicitly rather
	# than racing against a stack that is being torn down.
	Invoke-Tool -Name 'continue' -Arguments @{ session_id = $sessionId } | Out-Null
	$running = Invoke-Tool -Name 'get_callstack' -Arguments @{ session_id = $sessionId } -ExpectError
	Assert-That 'get_callstack on a running target is refused' ($running -match 'not_paused|Pause the session')

	Write-Host "== clear_breakpoints ==" -ForegroundColor Cyan
	$cleared = Invoke-Tool -Name 'clear_breakpoints' -Arguments @{}
	Assert-That 'clear_breakpoints reports what it removed' ($cleared.removed -ge 1) "(removed=$($cleared.removed))"
	$after = Invoke-Tool -Name 'list_breakpoints' -Arguments @{} -AsText
	Assert-That 'no breakpoints remain' ($after -eq '[]') "(payload $after)"

	Write-Host "== detach leaves the target alive ==" -ForegroundColor Cyan
	$cleanupStopPoint = Invoke-Tool -Name 'set_breakpoint' -Arguments @{ session_id = $sessionId; module = 'Milestone1Target.exe'; type = 'Milestone1Target.Program'; method = 'Tick' }
	$cleanupStop = Invoke-Tool -Name 'wait_for_stop' -Arguments @{ session_id = $sessionId; after_event_id = $cleanupStopPoint.cursor_event_id; timeout_ms = 8000 }
	Assert-That 'the object-ID teardown probe stops at a frame with a stable Main argument' (-not $cleanupStop.timed_out)
	$stepThread = @($cleanupStop.events)[0].thread_id
	$cleanupObject = Invoke-Tool -Name 'create_object_id' -Arguments @{ session_id = $sessionId; expression = 'commandLine'; process_id = $targetId; runtime_id = $program.runtime_guid; thread_id = $stepThread; frame_index = 1 }
	$null = Invoke-Tool -Name 'remove_breakpoint' -Arguments @{ breakpoint_id = $cleanupStopPoint.breakpoint_id }
	$detach = Invoke-Tool -Name 'detach' -Arguments @{ session_id = $sessionId }
	Assert-That 'detach reports detached, not terminated' ($detach.detached -and -not $detach.terminated)
	Start-Sleep -Milliseconds 750
	Assert-That 'the target survives detach' ($null -ne (Get-Process -Id $targetId -ErrorAction SilentlyContinue))
	$reattachProgram = @(Invoke-Tool -Name 'list_programs' -Arguments @{ process_ids = @($targetId) })[0]
	$reattached = Invoke-Tool -Name 'attach' -Arguments @{ program_id = $reattachProgram.program_id }
	$idsAfterDetach = @(Invoke-Tool -Name 'list_object_ids' -Arguments @{ session_id = $reattached.session_id; process_id = $targetId; runtime_id = $reattachProgram.runtime_guid } | ForEach-Object { $_ })
	Assert-That 'detach disposes runtime-scoped object IDs before reattach' (@($idsAfterDetach | Where-Object { $_.object_id -eq $cleanupObject.object_id }).Count -eq 0)
	$null = Invoke-Tool -Name 'detach' -Arguments @{ session_id = $reattached.session_id }
	$remaining = Invoke-Tool -Name 'list_sessions' -Arguments @{} -AsText
	Assert-That 'no sessions remain' (-not $remaining.Contains($sessionId)) "(payload $remaining)"
	$err = Invoke-Tool -Name 'get_session_state' -Arguments @{ session_id = $sessionId } -ExpectError
	Assert-That 'the detached session_id is no longer usable' ($err -match 'not active|No active')

	Write-Host "== launch, restart, terminate, unexpected exit ==" -ForegroundColor Cyan
	$missingLaunch = Invoke-Tool -Name 'launch' -Arguments @{ filename = (Join-Path $runDirectory 'missing.exe') } -ExpectError
	Assert-That 'launch rejects a missing target before invoking dnSpy' ($missingLaunch -match 'does not exist')
	$badEngine = Invoke-Tool -Name 'launch' -Arguments @{ filename = $targetExe; engine = 'coreclr' } -ExpectError
	Assert-That 'launch rejects an unsupported engine' ($badEngine -match 'cordebug.*unity')

	$launched = Invoke-Tool -Name 'launch' -Arguments @{ filename = $targetExe; engine = 'cordebug' }
	$launchedSessionId = $launched.session_id
	$launchedPid = [int]@($launched.process_ids)[0]
	Assert-That 'launch starts the target through dnSpy' ($launched.state -in @('running','paused') -and $launchedPid -gt 0)
	Assert-That 'launch creates a different process from the attached fixture' ($launchedPid -ne $targetId)

	$restarted = Invoke-Tool -Name 'restart' -Arguments @{ session_id = $launchedSessionId }
	$restartedPid = [int]@($restarted.process_ids)[0]
	Assert-That 'restart preserves the logical session' ($restarted.session_id -eq $launchedSessionId)
	Assert-That 'restart replaces the target process' ($restartedPid -gt 0 -and $restartedPid -ne $launchedPid) "(old=$launchedPid new=$restartedPid)"
	Start-Sleep -Milliseconds 500
	Assert-That 'the pre-restart process is gone' ($null -eq (Get-Process -Id $launchedPid -ErrorAction SilentlyContinue))

	$terminated = Invoke-Tool -Name 'terminate' -Arguments @{ session_id = $launchedSessionId }
	Assert-That 'terminate reports an exited session with explicit semantics' ($terminated.state -eq 'exited' -and $terminated.terminal_reason -eq 'terminated_by_client') "(state=$($terminated.state) reason=$($terminated.terminal_reason))"
	Start-Sleep -Milliseconds 500
	Assert-That 'terminate kills the launched target' ($null -eq (Get-Process -Id $restartedPid -ErrorAction SilentlyContinue))
	$terminateEvents = @(Invoke-Tool -Name 'get_events' -Arguments @{ session_id = $launchedSessionId; after_event_id = 0 }).events
	$terminateEvent = $terminateEvents | Where-Object { $_.kind -eq 'terminated' } | Select-Object -Last 1
	Assert-That 'terminate records a terminal event with the target PID' ($terminateEvent.terminal -and $terminateEvent.process_id -eq $restartedPid)
	Invoke-Tool -Name 'detach' -Arguments @{ session_id = $launchedSessionId } | Out-Null

	$exiting = Invoke-Tool -Name 'launch' -Arguments @{ filename = $targetExe; engine = 'cordebug'; command_line = '--exit-after-ms 2500 --exit-code 23' }
	$exitingSessionId = $exiting.session_id
	$exitingPid = [int]@($exiting.process_ids)[0]
	Start-Sleep -Milliseconds 3500
	$exited = Invoke-Tool -Name 'get_session_state' -Arguments @{ session_id = $exitingSessionId }
	Assert-That 'an unexpected target exit leaves an observable terminal session' ($exited.state -eq 'exited' -and $exited.terminal_reason -eq 'target_exited') "(state=$($exited.state) reason=$($exited.terminal_reason))"
	Assert-That 'the unexpected nonzero exit code is preserved' ($exited.exit_code -eq 23) "(exit_code=$($exited.exit_code))"
	$exitEvents = @(Invoke-Tool -Name 'get_events' -Arguments @{ session_id = $exitingSessionId; after_event_id = 0 }).events
	$exitEvent = $exitEvents | Where-Object { $_.kind -eq 'session_exited' } | Select-Object -Last 1
	Assert-That 'unexpected exit produces a terminal event' ($exitEvent.terminal -and $exitEvent.process_id -eq $exitingPid -and $exitEvent.exit_code -eq 23 -and $exitEvent.reason -eq 'target_exited')
	Assert-That 'the terminal event advances the session cursor' ($exitEvent.event_id -gt 0 -and $exited.last_event_id -ge $exitEvent.event_id)
	Invoke-Tool -Name 'detach' -Arguments @{ session_id = $exitingSessionId } | Out-Null

	# The success path needs a Mono/Unity target, which this harness cannot produce; see the manual
	# checklist in docs/DGSPY_UNITY_CHECKLIST.md. What is testable here is argument validation and the
	# failure path — that a refused endpoint faults with dnSpy's own reason instead of hanging or
	# reporting a healthy session. Run last: dnSpy pops a modal error box on connect failure (on its UI
	# thread, so it blocks neither the dispatcher nor this RPC, but it stays on screen).
	Write-Host "== attach_endpoint ==" -ForegroundColor Cyan
	$err = Invoke-Tool -Name 'attach_endpoint' -Arguments @{ address = '127.0.0.1' } -ExpectError
	Assert-That 'attach_endpoint without a port is refused' ($err -match 'port is required')
	$err = Invoke-Tool -Name 'attach_endpoint' -Arguments @{ port = 70000 } -ExpectError
	Assert-That 'attach_endpoint rejects an out-of-range port' ($err -match 'between 1 and 65535')
	$err = Invoke-Tool -Name 'attach_endpoint' -Arguments @{ port = 55555; engine = 'coreclr' } -ExpectError
	Assert-That 'attach_endpoint rejects an unsupported engine' ($err -match 'unity.*mono')

	# A port nothing is listening on. Bind and immediately release one so it is closed but plausible.
	$probe = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
	$probe.Start(); $deadPort = ([Net.IPEndPoint]$probe.LocalEndpoint).Port; $probe.Stop()
	$watch = [Diagnostics.Stopwatch]::StartNew()
	$faulted = Invoke-Tool -Name 'attach_endpoint' -Arguments @{ port = $deadPort; connection_timeout_ms = 2000 }
	$faultMs = $watch.ElapsedMilliseconds
	Assert-That 'a refused endpoint reports faulted' ($faulted.state -eq 'faulted') "(was $($faulted.state))"
	Assert-That 'the fault carries dnSpy''s own reason' ($faulted.fault_message -match 'connect') "(was '$($faulted.fault_message)')"
	Assert-That 'the fault is reported near the connection timeout' ($faultMs -lt 12000) "(took ${faultMs}ms)"
	Invoke-Tool -Name 'detach' -Arguments @{ session_id = $faulted.session_id } | Out-Null
	$remaining = Invoke-Tool -Name 'list_sessions' -Arguments @{} -AsText
	Assert-That 'a faulted session can be cleared with detach' (-not $remaining.Contains($faulted.session_id)) "(payload $remaining)"

	Write-Host ''
	if ($script:failures.Count -eq 0) {
		Write-Host "PASSED  $($script:checks) checks" -ForegroundColor Green
	}
	else {
		Write-Host "FAILED  $($script:failures.Count) of $($script:checks) checks" -ForegroundColor Red
		$script:failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
		Write-Host "Logs: $runDirectory"
		exit 1
	}
}
catch {
	Write-Host (($_ | Out-String) + "`nLogs: $runDirectory") -ForegroundColor Red
	exit 1
}
finally {
	foreach ($owned in @($gatewayProcess, $dnSpyProcess, $targetProcess, $detachTargetProcess)) {
		if ($null -ne $owned -and -not $owned.HasExited) { Stop-Process -Id $owned.Id -Force -ErrorAction SilentlyContinue }
	}
	Remove-Item Env:\DGSPY_TOKEN -ErrorAction SilentlyContinue
}
