# End-to-end test for the dgSpy milestone 1 slice.
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
$dnSpyProcess = $null; $gatewayProcess = $null; $targetProcess = $null
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
	$env:DGSPY_URL = $gatewayUrl
	$env:DGSPY_TOKEN = $token
	$dnSpyProcess = Start-Process -FilePath (Join-Path $dnSpyDir 'dnSpy.exe') -ArgumentList '--multiple' `
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

	Write-Host "== discovery ==" -ForegroundColor Cyan
	$tools = @((Invoke-Mcp -Method 'tools/list' -Parameters @{}).tools | ForEach-Object { $_.name })
	foreach ($expected in 'list_programs','attach','attach_endpoint','detach','list_sessions','get_session_state','pause','continue','set_il_breakpoint','list_breakpoints','remove_breakpoint','clear_breakpoints','wait_for_stop','list_threads','get_callstack','get_frame') {
		Assert-That "tools/list advertises $expected" ($tools -contains $expected)
	}

	$watch = [Diagnostics.Stopwatch]::StartNew()
	$programs = @(Invoke-Tool -Name 'list_programs' -Arguments @{ process_ids = @($targetId) })
	$filteredMs = $watch.ElapsedMilliseconds
	Assert-That 'a pid-filtered listing finds the target' ($programs.Count -ge 1 -and $programs[0].pid -eq $targetId)
	Assert-That 'a pid-filtered listing is fast' ($filteredMs -lt 1000) "(took ${filteredMs}ms)"
	$program = $programs[0]
	Assert-That 'program_id carries no dnSpy type name' (-not $program.program_id.Contains('RuntimeId')) "(was $($program.program_id))"
	Assert-That 'runtime_guid is distinct from runtime_kind_guid' ($program.runtime_guid -ne $program.runtime_kind_guid)

	Write-Host "== attach and session tracking ==" -ForegroundColor Cyan
	$session = Invoke-Tool -Name 'attach' -Arguments @{ program_id = $program.program_id }
	$sessionId = $session.session_id
	Assert-That 'attach reports a live state, not exited' ($session.state -in @('running','paused')) "(was $($session.state))"
	Assert-That 'attach reports the attached process' ($session.process_ids -contains $targetId)

	$sessions = @(Invoke-Tool -Name 'list_sessions' -Arguments @{})
	Assert-That 'list_sessions recovers the session' ($sessions.Count -eq 1 -and $sessions[0].session_id -eq $sessionId)
	Assert-That 'list_sessions reports the attached program' ($sessions[0].program_id -eq $program.program_id)
	Assert-That 'list_sessions says detaching is safe' ($sessions[0].can_detach_without_terminating)

	$err = Invoke-Tool -Name 'attach' -Arguments @{ program_id = $program.program_id } -ExpectError
	Assert-That 'a second attach is refused while a session is live' ($err -match 'Detach')

	$err = Invoke-Tool -Name 'get_session_state' -Arguments @{ session_id = 'not-a-session' } -ExpectError
	Assert-That 'an unknown session_id is refused' ($err -match 'not active|No active')

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
	Assert-That 'the stop event is after the cursor' (@($stop.events)[0].event_id -gt $cursor)

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
	$detach = Invoke-Tool -Name 'detach' -Arguments @{ session_id = $sessionId }
	Assert-That 'detach reports detached, not terminated' ($detach.detached -and -not $detach.terminated)
	Start-Sleep -Milliseconds 750
	Assert-That 'the target survives detach' ($null -ne (Get-Process -Id $targetId -ErrorAction SilentlyContinue))
	$remaining = Invoke-Tool -Name 'list_sessions' -Arguments @{} -AsText
	Assert-That 'no sessions remain' (-not $remaining.Contains($sessionId)) "(payload $remaining)"
	$err = Invoke-Tool -Name 'get_session_state' -Arguments @{ session_id = $sessionId } -ExpectError
	Assert-That 'the detached session_id is no longer usable' ($err -match 'not active|No active')

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
	foreach ($owned in @($gatewayProcess, $dnSpyProcess, $targetProcess)) {
		if ($null -ne $owned -and -not $owned.HasExited) { Stop-Process -Id $owned.Id -Force -ErrorAction SilentlyContinue }
	}
	Remove-Item Env:\DGSPY_TOKEN -ErrorAction SilentlyContinue
}
