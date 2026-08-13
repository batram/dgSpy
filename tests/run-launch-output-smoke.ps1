# Live smoke for the two launch-side defects:
#
#   1. A launched console program's stdout must reach get_output / wait_for_output, and must be
#      distinguishable from the host's own audit records.
#   2. launch must be safe to repeat: a second launch of the same image adopts the session it already
#      started instead of creating a second debuggee.
#
# The target mirrors every line it prints to puzzlebox.log, so this asserts against the program's own
# record rather than against dgSpy's report of it.
#
# ASCII-only by policy: an em dash in a .ps1 parses as a stray quote under CP1252.
param(
	[int]$GatewayPort = 17352,
	[int]$RpcPort = 0,
	[string]$TargetExe = 'C:\Users\mjb\AppData\Local\Temp\svc-puzzlebox\PuzzleBox.exe',
	# Control run: launch with redirect_output=false, skip the output assertions, and still report how long
	# the target takes to resume after a pause. That is what tells a slow resume apart from one caused by
	# capturing the console.
	[switch]$NoRedirect
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
$configuredLayout = [Environment]::GetEnvironmentVariable('DGSPY_LAYOUT_ROOT')
if ([string]::IsNullOrWhiteSpace($configuredLayout)) { throw 'DGSPY_LAYOUT_ROOT must name a completed DgSpyTool layout.' }
$dnSpyDir = [IO.Path]::GetFullPath($configuredLayout)
$gatewayDll = Join-Path $dnSpyDir 'bin\dgSpy.Gateway.dll'
$runDirectory = Join-Path ([IO.Path]::GetTempPath()) ('dgspy-launch-output-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $runDirectory | Out-Null
# The target writes its ground-truth log here, so each run gets a fresh one instead of appending to a
# previous run's lines and making the comparison meaningless.
$auditDir = Join-Path $runDirectory 'grader'
New-Item -ItemType Directory -Path $auditDir | Out-Null
$auditLog = Join-Path $auditDir 'puzzlebox.log'

$token = [Guid]::NewGuid().ToString('N') + [Guid]::NewGuid().ToString('N')
$dnSpyProcess = $null; $gatewayProcess = $null; $sessionId = $null; $launchedPids = @()

. "$PSScriptRoot\TestSupport\McpClient.ps1"
Initialize-McpClient -GatewayUrl $gatewayUrl -Token $token

function Get-ProgramLines {
	param([string]$SessionId)
	@(Invoke-Tool -Name 'get_output' -Arguments @{ session_id = $SessionId } |
		ForEach-Object { $_.messages } |
		Where-Object { $_.category -eq 'StandardOutput' -or $_.category -eq 'StandardError' } |
		ForEach-Object { $_.message })
}

# wait_for_output is the tool under test, so drive the wait with it rather than with a sleep: if it
# does not carry program output, this loop is what says so.
function Wait-ForProgramLine {
	param([string]$SessionId, [string]$Pattern, [int]$TimeoutSeconds = 180)
	$deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
	$cursor = 0
	do {
		$result = Invoke-Tool -Name 'wait_for_output' -Arguments @{ session_id = $SessionId; after_output_id = $cursor; timeout_ms = 2000 }
		$cursor = $result.last_output_id
		$seen = Get-ProgramLines -SessionId $SessionId
		if (@($seen | Where-Object { $_ -like $Pattern }).Count -gt 0) { return $true }
	} until ([DateTime]::UtcNow -gt $deadline)
	# A bare "did not arrive" cannot distinguish output that never reached the caller from a target that
	# never printed. Say what did arrive.
	Write-Host "        no line matching '$Pattern' in $TimeoutSeconds s; program lines so far: $((Get-ProgramLines -SessionId $SessionId) -join ' | ')" -ForegroundColor DarkYellow
	return $false
}

# The control run has no captured output by design, so it follows the target's own log instead.
function Wait-ForLoggedLine {
	param([string]$Path, [string]$Pattern, [int]$TimeoutSeconds = 180)
	return Wait-Until { @(Get-Content -LiteralPath $Path -ErrorAction SilentlyContinue | Where-Object { $_ -like "* $Pattern" }).Count -gt 0 } $TimeoutSeconds
}

# The target only advances when the debugger writes Gate.Stage, and only a paused target can be
# written to. Pause, assign, resume.
function Set-Stage {
	param([string]$SessionId, [int]$Stage)
	$state = Invoke-Tool -Name 'get_session_state' -Arguments @{ session_id = $SessionId }
	if ($state.state -eq 'running') { $null = Invoke-MutatingTool -Name 'pause' -Arguments @{ session_id = $SessionId } }
	# Assigning an int to a static int field is a direct write, but it still needs an evaluable managed
	# frame. Find the frame in Main rather than probing every thread and frame: the brute-force version
	# spent over a minute on refused func-evals, which shifted the caller's wait window far enough that
	# output which did arrive looked like output that never came.
	$thread = $null; $frame = -1; $stacks = @()
	foreach ($candidate in @(Invoke-Tool -Name 'list_threads' -Arguments @{ session_id = $SessionId } | ForEach-Object { $_ })) {
		$frames = @(Invoke-Tool -Name 'get_callstack' -Arguments @{ session_id = $SessionId; thread_id = $candidate.thread_id; max_frames = 12 } | ForEach-Object { $_ })
		$stacks += "$($candidate.thread_id): " + (@($frames | ForEach-Object { $_.name }) -join ' <- ')
		for ($i = 0; $i -lt $frames.Count; $i++) {
			# dnSpy formats frames as "Void Program.Main()" - return type and declaring type, no namespace.
			if ($frames[$i].name -like '*Program.Main*') { $thread = $candidate; $frame = $i; break }
		}
		if ($thread) { break }
	}
	if (-not $thread) { throw "No paused thread has a Program.Main frame to evaluate against. Stacks:`n  " + ($stacks -join "`n  ") }
	$null = Invoke-MutatingTool -Name 'set_value' -Arguments @{
		session_id = $SessionId; expression = 'PuzzleBox.Gate.Stage'; value = "$Stage"; thread_id = $thread.thread_id; frame_index = $frame
	}
	# Read the field back through the same paused frame. Without this, a set_value that reports success
	# but lands nowhere useful is indistinguishable from output that never reached the caller, and the
	# failure gets blamed on the tool under test.
	$readback = '(unread)'
	try { $readback = (Invoke-Tool -Name 'evaluate' -Arguments @{ session_id = $SessionId; expression = 'PuzzleBox.Gate.Stage'; thread_id = $thread.thread_id; frame_index = $frame }).value } catch { $readback = "(threw: $($_.Exception.Message))" }
	$null = Invoke-MutatingTool -Name 'continue' -Arguments @{ session_id = $SessionId }
	# This used to remain reported paused until the next evaluation. The launch call now returns only
	# after its requested entry-point stop, so the pause/write/resume sequence starts from one stable stop
	# and continue must make forward progress without another evaluation nudging it.
	$clock = [Diagnostics.Stopwatch]::StartNew()
	$resumed = Wait-Until { (Invoke-Tool -Name 'get_session_state' -Arguments @{ session_id = $SessionId }).state -eq 'running' } 5
	$clock.Stop()
	$state = (Invoke-Tool -Name 'get_session_state' -Arguments @{ session_id = $SessionId }).state
	Write-Host "        stage=$Stage written on $($thread.thread_id)/frame$frame, read back '$readback', resumed=$resumed in $([int]$clock.Elapsed.TotalSeconds) s, state=$state"
	if ($Stage -ne 9) {
		Assert-That "continue resumes promptly after pause plus set_value (stage $Stage)" ($resumed -and $state -eq 'running') `
			"(resumed=$resumed state=$state elapsed=$([int]$clock.Elapsed.TotalSeconds)s)"
	}
}

try {
	Write-Section 'deploy and start'
	if (-not (Test-Path -LiteralPath $TargetExe)) { throw "Target not found: $TargetExe" }

	$env:DGSPY_RPC_PORT = [string]$RpcPort
	$env:DGSPY_URL = $gatewayUrl
	$env:DGSPY_TOKEN = $token
	# dnSpy launches the target as its child, so the child inherits this and writes its ground truth
	# where this run can read it.
	$env:PUZZLEBOX_AUDIT_DIR = $auditDir
	$dnSpyProcess = Start-Process -FilePath (Join-Path $dnSpyDir 'dnSpy.exe') -ArgumentList '--multiple','--dgspy-no-window-activation' `
		-WorkingDirectory $dnSpyDir -PassThru
	$gatewayProcess = Start-Process -FilePath 'dotnet' -ArgumentList ('"' + $gatewayDll + '"') `
		-WorkingDirectory (Split-Path $gatewayDll) -PassThru `
		-RedirectStandardOutput (Join-Path $runDirectory 'gateway.out') -RedirectStandardError (Join-Path $runDirectory 'gateway.err')
	if (-not (Wait-Until { try { $null -ne (Invoke-RestMethod ($gatewayUrl + '/health') -TimeoutSec 1) } catch { $false } } 30)) {
		throw 'Gateway health endpoint did not become ready.'
	}
	if (-not (Wait-Until { @(Get-NetTCPConnection -State Listen -LocalPort $RpcPort -ErrorAction SilentlyContinue).Count -gt 0 } 60)) {
		throw "Extension RPC endpoint 127.0.0.1:$RpcPort did not become ready. Is the extension deployed?"
	}

	Write-Section 'launch and read the program stdout'
	$launched = Invoke-Tool -Name 'launch' -Arguments @{ filename = $TargetExe; break_at = 'entry_point'; redirect_output = (-not $NoRedirect) }
	$sessionId = $launched.session_id
	$script:activeSessionId = $sessionId
	Assert-That 'launch returned a session' (-not [string]::IsNullOrWhiteSpace($sessionId))
	Assert-That 'launch reports exactly one process' (@($launched.process_ids).Count -eq 1) "(process_ids $($launched.process_ids -join ','))"
	$launchedPid = @($launched.process_ids)[0]
	$launchedPids = @($launched.process_ids)

	Assert-That 'launch waits for its requested entry-point stop' ($launched.state -eq 'paused' -and -not [string]::IsNullOrWhiteSpace($launched.stop_id)) `
		"(state=$($launched.state) stop_id=$($launched.stop_id))"
	$initialStop = Invoke-Tool -Name 'get_stop_reason' -Arguments @{ session_id = $sessionId }
	Assert-That 'the initial launch stop is the entry point' ($initialStop.stop_reason -eq 'entry_point' -and $initialStop.process_id -eq $launchedPid) `
		"(reason=$($initialStop.stop_reason) process_id=$($initialStop.process_id))"
	$null = Invoke-MutatingTool -Name 'continue' -Arguments @{ session_id = $sessionId }
	if ($NoRedirect) {
		Assert-That 'the control target started' (Wait-ForLoggedLine -Path $auditLog -Pattern 'READY pid=*')
		Assert-That 'redirect_output=false really carries no program output' (@(Get-ProgramLines -SessionId $sessionId).Count -eq 0) `
			"(saw: $((Get-ProgramLines -SessionId $sessionId) -join ' | '))"
	}
	else {
		Assert-That 'the program READY line reaches wait_for_output' (Wait-ForProgramLine -SessionId $sessionId -Pattern 'READY pid=*')
		$ready = @(Get-ProgramLines -SessionId $sessionId | Where-Object { $_ -like 'READY pid=*' })[0]
		Assert-That 'the READY line names the process launch reported' ($ready -eq "READY pid=$launchedPid") "(line '$ready', launch said $launchedPid)"

		$readyMessage = @(Invoke-Tool -Name 'get_output' -Arguments @{ session_id = $sessionId } |
			ForEach-Object { $_.messages } | Where-Object { $_.message -like 'READY pid=*' })[0]
		Assert-That 'program output is categorized StandardOutput' ($readyMessage.category -eq 'StandardOutput') "(category $($readyMessage.category))"
		Assert-That 'program output carries the target process id' ($readyMessage.process_id -eq $launchedPid) "(process_id $($readyMessage.process_id))"
	}

	# Drive the stages that make the target print more than its startup line. Set-Stage also asserts that
	# each nonterminal pause/write/continue cycle resumes without needing a later evaluation.
	Write-Section 'drive the target through its printing stages'
	Set-Stage -SessionId $sessionId -Stage 1
	Set-Stage -SessionId $sessionId -Stage 4

	Write-Section 'an interrupted launch is not a second debuggee'
	# Stand in for the cancelled call: the process is already up and the caller asks again. The tool
	# must return the session it already started, not create another PuzzleBox.
	$before = @(Get-Process -Name 'PuzzleBox' -ErrorAction SilentlyContinue).Count
	$again = Invoke-Tool -Name 'launch' -Arguments @{ filename = $TargetExe; break_at = 'entry_point'; redirect_output = (-not $NoRedirect) }
	Assert-That 'a repeat launch returns the same session' ($again.session_id -eq $sessionId) "(got $($again.session_id))"
	Assert-That 'a repeat launch reports the same process' ((@($again.process_ids) -join ',') -eq "$launchedPid") "(process_ids $($again.process_ids -join ','))"
	Start-Sleep -Seconds 2
	$after = @(Get-Process -Name 'PuzzleBox' -ErrorAction SilentlyContinue).Count
	Assert-That 'no second PuzzleBox process was created' ($after -eq $before) "(before $before, after $after)"

	$sessions = @(Invoke-Tool -Name 'list_sessions' -Arguments @{} | ForEach-Object { $_ })
	Assert-That 'list_sessions exposes the launch program_id an interrupted caller must look for' `
		(@($sessions | Where-Object { $_.program_id -like 'launch:cordebug:*PuzzleBox.exe' }).Count -eq 1) `
		"(program_ids $(@($sessions | ForEach-Object { $_.program_id }) -join ' | '))"

	# Adoption must not remove the ability to run a second copy on purpose. Do this last in the section:
	# the extra target adds a second program_id to the session, which the check above reads.
	$second = Invoke-Tool -Name 'launch' -Arguments @{ filename = $TargetExe; break_at = 'none'; adopt_existing = $false }
	Assert-That 'adopt_existing=false really starts a second process' (@($second.process_ids).Count -eq 2) "(process_ids $($second.process_ids -join ','))"
	$secondPid = @($second.process_ids | Where-Object { $_ -ne $launchedPid })[0]
	$launchedPids = @($second.process_ids)
	if ($secondPid) {
		$null = Invoke-MutatingTool -Name 'terminate' -Arguments @{ session_id = $sessionId; process_id = $secondPid }
		$null = Wait-Until { @(Invoke-Tool -Name 'get_session_state' -Arguments @{ session_id = $sessionId }).process_ids.Count -eq 1 } 20
	}

	Write-Section 'the program record agrees'
	# Stage 9 makes the target print DONE and exit. Its own exit is the unambiguous end of output, so wait
	# for that rather than for any particular line.
	Set-Stage -SessionId $sessionId -Stage 9
	$exited = Wait-Until { (Invoke-Tool -Name 'get_session_state' -Arguments @{ session_id = $sessionId }).state -eq 'exited' } 180
	Assert-That 'the target ran to completion and exited' $exited
	$reported = if ($NoRedirect) { @() } else { Get-ProgramLines -SessionId $sessionId }
	if (-not $NoRedirect) {
		foreach ($expected in 'FINGERPRINT *','VERDICT *','DONE') {
			Assert-That "a driven stage line matching '$expected' was captured" (@($reported | Where-Object { $_ -like $expected }).Count -gt 0) `
				"(captured: $($reported -join ' | '))"
		}
	}
	# Only the mirrored stdout lines carry a timestamp prefix. The GRADER line does not, and it is
	# deliberately never printed, so match on the prefix rather than stripping it off everything.
	$truth = @(Get-Content -LiteralPath $auditLog -ErrorAction SilentlyContinue |
		Where-Object { $_ -match '^\d\d:\d\d:\d\d\.\d\d\d ' } | ForEach-Object { ($_ -split ' ', 2)[1] })
	Assert-That 'the target wrote its own record' (@($truth).Count -gt 0)
	if ($NoRedirect) {
		Write-Host "  control run: program logged $(@($truth).Count) lines, none captured by design"
	}
	else {
		$missing = @($truth | Where-Object { $reported -notcontains $_ })
		Assert-That 'every line the program logged also reached get_output' (@($missing).Count -eq 0) "(missing: $($missing -join ' | '))"
		Write-Host "  program logged $(@($truth).Count) lines; get_output carried $(@($reported).Count)"
	}
}
catch {
	# A smoke that dies mid-run must still say why. Without this the run reports only the checks it
	# reached, which reads as a clean partial pass.
	Assert-That 'the smoke ran to completion' $false "(aborted: $($_.Exception.Message))"
	Write-Host $_.ScriptStackTrace -ForegroundColor DarkRed
}
finally {
	Write-Section 'teardown'
	if ($sessionId) {
		try { Invoke-MutatingTool -Name 'terminate' -Arguments @{ session_id = $sessionId } | Out-Null } catch { Write-Host "  terminate skipped: $($_.Exception.Message)" -ForegroundColor DarkYellow }
	}
	foreach ($process in @($gatewayProcess, $dnSpyProcess)) {
		if ($process -and -not $process.HasExited) { try { Stop-Process -Id $process.Id -Force } catch { } }
	}
	# Only the pids this run launched. The reproduction target is shared, and killing every process by
	# name would take out a concurrent run's target as well.
	foreach ($id in $launchedPids) { try { Stop-Process -Id $id -Force -ErrorAction Stop } catch { } }
	foreach ($log in 'gateway.err') {
		$path = Join-Path $runDirectory $log
		if (Test-Path -LiteralPath $path) {
			$tail = @(Get-Content -LiteralPath $path -Tail 15 -ErrorAction SilentlyContinue)
			if ($tail.Count) { Write-Host "  ---- $log ----" -ForegroundColor DarkGray; $tail | ForEach-Object { Write-Host "        $_" -ForegroundColor DarkGray } }
		}
	}
	Write-Host "  run directory: $runDirectory"
}

if ($script:failures.Count -gt 0) {
	Write-Host "FAILED $($script:failures.Count) of $script:checks checks" -ForegroundColor Red
	$script:failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
	exit 1
}
Write-Host "PASSED all $script:checks checks" -ForegroundColor Green
