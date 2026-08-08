# Live measurement for the MCP response-shape change set.
#
# The claim under test is a call count, so this script counts calls. It drives PuzzleBox through the
# same three stages twice against one session:
#
#   baseline  reads get_session_state before every guarded mutation, which is what a caller had to do
#             when mutating responses returned no counters
#   vector    takes every guard and every event cursor from the previous response's `versions`
#
# The difference between the two totals is the interposed-read tax the change set removes. It then
# checks the two capabilities agents could not find: a condition attached through update_breakpoint,
# and a single exception policy confirmed removed through a name-scoped list.
#
# ASCII-only by policy: an em dash in a .ps1 parses as a stray quote under CP1252.
param(
	[int]$GatewayPort = 17361,
	[int]$RpcPort = 0,
	[string]$TargetExe = 'C:\Users\mjb\AppData\Local\Temp\svc-puzzlebox\PuzzleBox.exe',
	[ValidateSet('net10.0-windows','net48')][string]$TargetFramework = 'net10.0-windows'
)

$ErrorActionPreference = 'Stop'
$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new()
$env:MSBUILDDISABLENODEREUSE = '1'

$repoRoot = Split-Path $PSScriptRoot -Parent
$gatewayUrl = "http://127.0.0.1:$GatewayPort"
if ($RpcPort -eq 0) {
	$probe = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
	$probe.Start(); $RpcPort = ([Net.IPEndPoint]$probe.LocalEndpoint).Port; $probe.Stop()
}
$dnSpyDir = if ($TargetFramework -eq 'net48') {
	Join-Path $repoRoot 'dnSpy\dnSpy\bin\Release\net48'
} else {
	Join-Path $repoRoot 'dnSpy\dnSpy\bin\Release\net10.0-windows\win-x64\publish'
}
$gatewayDll = Join-Path $repoRoot 'dgSpy.Gateway\bin\Release\net10.0\dgSpy.Gateway.dll'
$runDirectory = Join-Path ([IO.Path]::GetTempPath()) ('dgspy-shape-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $runDirectory | Out-Null
$token = [Guid]::NewGuid().ToString('N') + [Guid]::NewGuid().ToString('N')
$dnSpyProcess = $null; $gatewayProcess = $null; $targetProcess = $null

. "$PSScriptRoot\TestSupport\McpClient.ps1"
Initialize-McpClient -GatewayUrl $gatewayUrl -Token $token

# Every tool call this script makes goes through here, so the count is the measurement rather than
# something reconstructed from a log afterwards.
$script:callCount = 0
$script:stateReads = 0
function Invoke-Counted {
	param([string]$Name, [hashtable]$Arguments)
	$script:callCount++
	if ($Name -eq 'get_session_state') { $script:stateReads++ }
	return Invoke-Tool -Name $Name -Arguments $Arguments
}
function Reset-Counters { $script:callCount = 0; $script:stateReads = 0 }

function Get-TargetLines { @(Get-Content (Join-Path $runDirectory 'target.out') -ErrorAction SilentlyContinue) }

# An expression is compiled against the selected frame's module, so `PuzzleBox.Gate.Stage` does not
# resolve from the mscorlib frame a paused target is usually sitting in -- a pause lands in
# Thread.Sleep, and the answer there is CS0103, not a missing field. set_value reports that in band
# as assigned=false with compiler_error=true rather than as a tool error, so a caller that does not
# read `assigned` records a write that never happened. This harness did exactly that on its first
# run and reported three stages driven when none were. Both drivers therefore select the target's
# own frame and assert on `assigned`.
$script:driveThread = $null; $script:driveFrame = 0
function Select-TargetFrame {
	param([string]$SessionId)
	foreach ($thread in @(Invoke-Tool -Name 'list_threads' -Arguments @{ session_id = $SessionId } | ForEach-Object { $_ })) {
		$frames = @(Invoke-Tool -Name 'get_callstack' -Arguments @{ session_id = $SessionId; thread_id = $thread.thread_id; max_frames = 12 } | ForEach-Object { $_ })
		for ($i = 0; $i -lt $frames.Count; $i++) {
			if ($frames[$i].name -like '*Program.Main*') { $script:driveThread = $thread.thread_id; $script:driveFrame = $i; return $true }
		}
	}
	return $false
}
function Set-Stage {
	param([string]$SessionId, [int]$Stage, $Versions)
	$assigned = Invoke-Counted 'set_value' @{ session_id = $SessionId; expression = 'PuzzleBox.Gate.Stage'; value = "$Stage"
		thread_id = $script:driveThread; frame_index = $script:driveFrame
		expected_execution_version = $Versions.execution_version; expected_stop_id = $Versions.stop_id }
	Assert-That "stage $Stage was actually written" ($assigned.assigned -eq $true) "($($assigned.error))"
	return $assigned
}

# One stage transition, guards read from a fresh get_session_state each time. This is the shape a
# caller was forced into when a mutating response told it nothing about the state it had just moved.
function Step-StageBaseline {
	param([string]$SessionId, [int]$Stage)
	$state = Invoke-Counted 'get_session_state' @{ session_id = $SessionId }
	$null = Invoke-Counted 'pause' @{ session_id = $SessionId; expected_execution_version = $state.execution_version }
	$state = Invoke-Counted 'get_session_state' @{ session_id = $SessionId }
	$null = Set-Stage -SessionId $SessionId -Stage $Stage -Versions $state
	$state = Invoke-Counted 'get_session_state' @{ session_id = $SessionId }
	$null = Invoke-Counted 'continue' @{ session_id = $SessionId; expected_execution_version = $state.execution_version }
}

# The same transition with every guard taken from the response before it.
function Step-StageVector {
	param([string]$SessionId, [int]$Stage, $Versions)
	$paused = Invoke-Counted 'pause' @{ session_id = $SessionId; expected_execution_version = $Versions.execution_version }
	$assigned = Set-Stage -SessionId $SessionId -Stage $Stage -Versions $paused.versions
	$resumed = Invoke-Counted 'continue' @{ session_id = $SessionId; expected_execution_version = $assigned.versions.execution_version }
	return $resumed.versions
}

try {
	Write-Section 'build and deploy'
	& (Join-Path $repoRoot 'build-dgspy.ps1') -TargetFramework $TargetFramework -DnSpyDir $dnSpyDir | Out-Null
	if (-not (Test-Path -LiteralPath $TargetExe)) { throw "PuzzleBox is not at $TargetExe." }

	Write-Section 'start target, dnSpy, gateway'
	$targetProcess = Start-Process -FilePath $TargetExe -WindowStyle Hidden -PassThru `
		-RedirectStandardOutput (Join-Path $runDirectory 'target.out') `
		-RedirectStandardError (Join-Path $runDirectory 'target.err')
	if (-not (Wait-Until { @(Get-TargetLines | Where-Object { $_ -like 'READY*' }).Count -ge 1 } 20)) {
		throw 'PuzzleBox did not publish its READY line.'
	}
	$env:DGSPY_RPC_PORT = [string]$RpcPort
	$env:DGSPY_URL = $gatewayUrl
	$env:DGSPY_TOKEN = $token
	$dnSpyProcess = Start-Process -FilePath (Join-Path $dnSpyDir 'dnSpy.exe') `
		-ArgumentList '--multiple','--dgspy-no-window-activation' -WorkingDirectory $dnSpyDir -WindowStyle Hidden -PassThru
	$gatewayProcess = Start-Process -FilePath 'dotnet' -ArgumentList ('"' + $gatewayDll + '"') `
		-WorkingDirectory (Split-Path $gatewayDll) -WindowStyle Hidden -PassThru `
		-RedirectStandardOutput (Join-Path $runDirectory 'gateway.out') -RedirectStandardError (Join-Path $runDirectory 'gateway.err')
	if (-not (Wait-Until { try { $null -ne (Invoke-RestMethod ($gatewayUrl + '/health') -TimeoutSec 1) } catch { $false } } 40)) {
		throw 'Gateway health endpoint did not become ready.'
	}
	if (-not (Wait-Until { @(Get-NetTCPConnection -State Listen -LocalPort $RpcPort -ErrorAction SilentlyContinue).Count -gt 0 } 60)) {
		throw "Extension RPC endpoint 127.0.0.1:$RpcPort did not become ready."
	}

	Write-Section 'attach'
	$programs = @(Invoke-Tool -Name 'list_programs' -Arguments @{ process_ids = @($targetProcess.Id) } | ForEach-Object { $_ })
	Assert-That 'list_programs finds PuzzleBox' ($programs.Count -ge 1)
	$attached = Invoke-Tool -Name 'attach' -Arguments @{ program_id = $programs[0].program_id }
	$sessionId = $attached.session_id
	Assert-That 'attach returns a session' (-not [string]::IsNullOrWhiteSpace($sessionId))
	# The first mutation of a brand new session needs counters the caller cannot have yet. Without
	# this, every session opened with a get_session_state that only restated what attach had just done.
	Assert-That 'attach echoes the version vector' ($null -ne $attached.versions)
	Assert-That 'attach reports an execution_version' ($null -ne $attached.versions.execution_version)

	# Breakpoints are dnSpy-global and outlive both the session and the host process, so an earlier run
	# of this script leaves its own behind and the next run inherits them. Three showed up that way
	# during development, silently changing where the target stopped.
	$before = Invoke-Tool -Name 'get_session_state' -Arguments @{ session_id = $sessionId }
	$null = Invoke-Tool -Name 'clear_breakpoints' -Arguments @{ session_id = $sessionId; expected_breakpoints_version = $before.breakpoints_version }

	Write-Section 'select the frame the target expressions compile against'
	$firstPause = Invoke-Tool -Name 'pause' -Arguments @{ session_id = $sessionId; expected_execution_version = $attached.versions.execution_version }
	Assert-That 'the target has a frame in PuzzleBox itself' (Select-TargetFrame -SessionId $sessionId)
	Write-Host "        driving through thread $script:driveThread frame $script:driveFrame" -ForegroundColor Gray
	$null = Invoke-Tool -Name 'continue' -Arguments @{ session_id = $sessionId; expected_execution_version = $firstPause.versions.execution_version }

	Write-Section 'three stages, guards read from get_session_state'
	Reset-Counters
	foreach ($stage in 1,2,3) {
		Step-StageBaseline -SessionId $sessionId -Stage $stage
		$null = Wait-Until { @(Get-TargetLines).Count -ge ($stage + 1) } 5
	}
	$baselineCalls = $script:callCount; $baselineReads = $script:stateReads
	Write-Host "        baseline: $baselineCalls calls, $baselineReads of them get_session_state" -ForegroundColor Gray

	Write-Section 'three stages, guards read from the previous response'
	$state = Invoke-Tool -Name 'get_session_state' -Arguments @{ session_id = $sessionId }
	Reset-Counters
	$versions = $state
	# 4 and 5 are the two stages the baseline pass did not reach; 1 again is observable because the
	# target reprints whenever Stage differs from the value it last acted on.
	foreach ($stage in 4,5,1) {
		$before = @(Get-TargetLines).Count
		$versions = Step-StageVector -SessionId $sessionId -Stage $stage -Versions $versions
		Assert-That "stage $stage response carries a version vector" ($null -ne $versions)
		$null = Wait-Until { @(Get-TargetLines).Count -gt $before } 5
	}
	$vectorCalls = $script:callCount; $vectorReads = $script:stateReads
	Write-Host "        vector:   $vectorCalls calls, $vectorReads of them get_session_state" -ForegroundColor Gray
	Assert-That 'driving three stages needs no interposed state read' ($vectorReads -eq 0) "(was $baselineReads)"
	Assert-That 'the vector driver makes strictly fewer calls' ($vectorCalls -lt $baselineCalls) "($vectorCalls vs $baselineCalls)"

	Write-Section 'the target really moved through the stages'
	$lines = Get-TargetLines
	foreach ($expected in 'FINGERPRINT','UNLOCK','DESCEND','VERDICT','CAUGHT') {
		Assert-That "the target printed $expected" (@($lines | Where-Object { $_ -like "$expected*" }).Count -ge 1)
	}

	Write-Section 'a condition attached through update_breakpoint'
	# The capability an agent concluded did not exist, then hand-computed around and got wrong by one
	# iteration. Gate.Probe runs its loop eight times in one call, so "stop at i == 5" is exactly the
	# question that was answered incorrectly.
	$state = Invoke-Tool -Name 'get_session_state' -Arguments @{ session_id = $sessionId }
	if ($state.state -eq 'running') {
		$paused = Invoke-Tool -Name 'pause' -Arguments @{ session_id = $sessionId; expected_execution_version = $state.execution_version }
		$state = $paused.versions
	}
	Write-Section 'a refused CorDebug offset snaps only after its stable verdict'
	$verdictIl = Invoke-Tool -Name 'get_il' -Arguments @{ session_id = $sessionId; module = 'PuzzleBox'; type = 'PuzzleBox.Gate'; method = 'Verdict' }
	$refused = @($verdictIl.instructions | Where-Object { $_.offset -eq 6 -and -not $_.is_sequence_point })[0]
	$nextPoint = @($verdictIl.instructions | Where-Object { $_.is_sequence_point -and $_.offset -gt 6 } | Sort-Object offset | Select-Object -First 1)[0]
	Assert-That 'Verdict offset 0x6 is non-sequence IL before a legal point' ($null -ne $refused -and $null -ne $nextPoint) "(refused=$($refused.offset), next=$($nextPoint.offset))"
	$snapped = Invoke-Tool -Name 'set_breakpoint' -Arguments @{ session_id = $sessionId; module = 'PuzzleBox'; type = 'PuzzleBox.Gate'; method = 'Verdict'
		il_offset = $refused.offset; expected_breakpoints_version = $state.breakpoints_version }
	Assert-That 'set_breakpoint reports the stable snapped CorDebug verdict' ($snapped.bound -and $snapped.severity -eq 'none' -and $snapped.snapped) "(bound=$($snapped.bound), severity=$($snapped.severity), message='$($snapped.message)')"
	Assert-That 'the refused offset snaps forward to the next sequence point' ($snapped.requested_il_offset -eq $refused.offset -and $snapped.il_offset -eq $nextPoint.offset) "(requested=$($snapped.requested_il_offset), actual=$($snapped.il_offset), next=$($nextPoint.offset))"
	$removedSnap = Invoke-Tool -Name 'remove_breakpoint' -Arguments @{ session_id = $sessionId; breakpoint_id = $snapped.breakpoint_id; expected_breakpoints_version = $snapped.versions.breakpoints_version }
	Assert-That 'the snapped regression breakpoint is removed' $removedSnap.removed
	$state = $removedSnap.versions
	# Select the loop body by source line, not "the first sequence point past 0". That shortcut lands on
	# `int marker = 0;`, which is ahead of the loop and where `i` is not in scope, so `i == 5` can never
	# be true and the breakpoint silently never stops -- a harness bug that reads exactly like the
	# product ignoring the condition. The assignment inside the loop is the only line that mentions both.
	# Anchor on the instruction, not on an index into the sequence points. "The first sequence point past
	# offset 0" lands on `int marker = 0;`, ahead of the loop and where `i` is not in scope, so `i == 5`
	# can never be true and the breakpoint silently never stops -- a harness bug that reads exactly like
	# the product ignoring the condition. `marker * 7` appears only in the loop body, so the last
	# sequence point at or before that multiply is the statement wanted.
	$il = Invoke-Tool -Name 'get_il' -Arguments @{ session_id = $sessionId; module = 'PuzzleBox'; type = 'PuzzleBox.Gate'; method = 'Probe' }
	$multiply = @($il.instructions | Where-Object { $_.opcode -eq 'mul' } | Select-Object -First 1)
	$bodyOffset = @($il.instructions | Where-Object { $_.is_sequence_point -and $null -ne $multiply -and $_.offset -le $multiply.offset } |
		Sort-Object offset | Select-Object -Last 1).offset
	Assert-That 'get_il names the sequence point of the Probe loop body' ($null -ne $bodyOffset) "(mul at $($multiply.offset), sequence point $bodyOffset)"
	$bp = Invoke-Tool -Name 'set_breakpoint' -Arguments @{ session_id = $sessionId; module = 'PuzzleBox'
		type = 'PuzzleBox.Gate'; method = 'Probe'; il_offset = $bodyOffset
		expected_breakpoints_version = $state.breakpoints_version }
	Assert-That 'set_breakpoint echoes the version vector' ($null -ne $bp.versions)
	# The wait cursor, captured here rather than after the resume. Probe is reached in microseconds, so
	# by the time `continue` returns its stamped last_event_id can already include the stop being waited
	# for, and waiting from it waits for the next one. This is what set_breakpoint's cursor_event_id is.
	$stopCursor = $bp.cursor_event_id
	$updated = Invoke-Tool -Name 'update_breakpoint' -Arguments @{ session_id = $sessionId; breakpoint_id = $bp.breakpoint_id
		condition = 'i == 5'; expected_breakpoints_version = $bp.versions.breakpoints_version }
	Assert-That 'update_breakpoint accepts the condition' ($null -ne $updated.versions)

	# Stage was last 1, so writing 3 makes the target run Descend -> Probe once more.
	$null = Select-TargetFrame -SessionId $sessionId
	$assigned = Set-Stage -SessionId $sessionId -Stage 3 -Versions $updated.versions
	$null = Invoke-Tool -Name 'continue' -Arguments @{ session_id = $sessionId; expected_execution_version = $assigned.versions.execution_version }
	$stopped = Invoke-Tool -Name 'wait_for_stop' -Arguments @{ session_id = $sessionId
		after_event_id = $stopCursor; timeout_ms = 10000 }
	# engine_hit_count is counted before the condition runs, so it separates the two ways this can fail:
	# a breakpoint the engine never reached, from one it reached eight times while the condition stayed
	# false. Without it a timeout says only "nothing happened".
	$bpState = @(Invoke-Tool -Name 'list_breakpoints' -Arguments @{} | ForEach-Object { $_ } | Where-Object { $_.breakpoint_id -eq $bp.breakpoint_id })[0]
	Assert-That 'the conditional breakpoint stopped the target' (-not $stopped.timed_out) "(engine_hit_count=$($bpState.engine_hit_count), bound=$($bpState.bound))"
	Assert-That 'wait_for_stop echoes the version vector' ($null -ne $stopped.versions)
	if (-not $stopped.timed_out) {
		$i = Invoke-Tool -Name 'evaluate' -Arguments @{ session_id = $sessionId; expression = 'i' }
		Assert-That 'it stopped at exactly the requested iteration' ($i.value -eq 5) "(i = $($i.value))"
		# 0, then 1, 9, 66, 466 after four more iterations: the value at the top of iteration five.
		$marker = Invoke-Tool -Name 'evaluate' -Arguments @{ session_id = $sessionId; expression = 'marker' }
		Assert-That 'the loop state matches that iteration' ($marker.value -eq 466) "(marker = $($marker.value))"
	}
	$cleanup = Invoke-Tool -Name 'remove_breakpoint' -Arguments @{ session_id = $sessionId
		breakpoint_id = $bp.breakpoint_id; expected_breakpoints_version = $stopped.versions.breakpoints_version }
	Assert-That 'remove_breakpoint says what it did' ($cleanup.removed -eq $true)

	Write-Section 'one exception policy, confirmed removed'
	$state = Invoke-Tool -Name 'get_session_state' -Arguments @{ session_id = $sessionId }
	$set = Invoke-Tool -Name 'set_exception_breakpoint' -Arguments @{ session_id = $sessionId
		name = 'PuzzleBox.PuzzleException'; stop_first_chance = $true
		expected_breakpoints_version = $state.breakpoints_version }
	$scoped = Invoke-Tool -Name 'list_exception_policies' -Arguments @{ session_id = $sessionId
		category = 'DotNet'; name = 'PuzzleBox.PuzzleException' }
	Assert-That 'a name-scoped read returns exactly the one entry' ($scoped.total -eq 1) "(total = $($scoped.total))"
	$removed = Invoke-Tool -Name 'remove_exception_policy' -Arguments @{ session_id = $sessionId
		category = 'DotNet'; name = 'PuzzleBox.PuzzleException'
		expected_breakpoints_version = $set.versions.breakpoints_version }
	Assert-That 'the removal says removed=true' ($removed.removed -eq $true)
	Assert-That 'the former flags are nested, not top level' ($removed.former_policy.stop_thrown -eq $true -and $null -eq $removed.stop_thrown)
	$after = Invoke-Tool -Name 'list_exception_policies' -Arguments @{ session_id = $sessionId
		category = 'DotNet'; name = 'PuzzleBox.PuzzleException' }
	Assert-That 'the same scoped read now proves it is gone' ($after.total -eq 0) "(total = $($after.total))"

	Write-Section 'detach'
	$state = Invoke-Tool -Name 'get_session_state' -Arguments @{ session_id = $sessionId }
	$null = Invoke-Tool -Name 'detach' -Arguments @{ session_id = $sessionId; expected_lifecycle_version = $state.lifecycle_version }
}
finally {
	foreach ($process in $gatewayProcess, $dnSpyProcess, $targetProcess) {
		if ($null -ne $process) { try { $process.Refresh(); if (-not $process.HasExited) { $process.Kill() } } catch { } }
	}
	Write-Host ''
	Write-Host "checks: $script:checks, failures: $($script:failures.Count)" -ForegroundColor Cyan
	foreach ($failure in $script:failures) { Write-Host "  $failure" -ForegroundColor Red }
	Write-Host "logs: $runDirectory" -ForegroundColor DarkGray
}
if ($script:failures.Count -gt 0) { exit 1 }
exit 0
