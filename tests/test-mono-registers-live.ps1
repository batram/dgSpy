$ErrorActionPreference = 'Stop'
$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new()
. (Join-Path $PSScriptRoot 'TestSupport\Invoke-DgSpyRpc.ps1')
$rpcPort = 7352
function Invoke-LiveRpc { param([string]$OperationName,[hashtable]$OperationArguments=@{},[int]$DeadlineSeconds=15) Invoke-DgSpyRpc -OperationName $OperationName -OperationArguments $OperationArguments -DeadlineSeconds $DeadlineSeconds -RpcPort $rpcPort }

$sessionId = $null
$processId = $null
$processIds = @()
try {
	$attached = Invoke-LiveRpc -OperationName 'attach_endpoint' -OperationArguments @{ address='127.0.0.1';port=55555;engine='unity';process_is_suspended=$false;connection_timeout_ms=10000 } -DeadlineSeconds 30
	$sessionId = $attached.session_id
	if ($attached.state -notin @('running','paused','mixed')) { throw "Unexpected attach state '$($attached.state)'." }
	$processIds = @((Invoke-LiveRpc -OperationName 'get_session_state' -OperationArguments @{session_id=$sessionId}).process_ids)

	$modules = @((Invoke-LiveRpc -OperationName 'list_modules' -OperationArguments @{session_id=$sessionId;name_pattern='UltimateGlorpExplorer'}).modules)
	$plugin = $modules | Where-Object { $_.filename -like '*UltimateGlorpExplorer*' } | Select-Object -First 1
	if (-not $plugin) { throw 'UltimateGlorpExplorer module was not loaded.' }
	$processId = $plugin.process_id
	$search = Invoke-LiveRpc -OperationName 'search_symbols' -OperationArguments @{session_id=$sessionId;pattern='Update';module=$plugin.filename;kinds=@('method');count=40} -DeadlineSeconds 60
	$candidates = @($search.symbols)
	if ($candidates.Count -eq 0) { throw 'No hot Update methods were found.' }

	Invoke-LiveRpc -OperationName 'clear_breakpoints' | Out-Null
	$cursor = (Invoke-LiveRpc -OperationName 'get_session_state' -OperationArguments @{session_id=$sessionId}).last_event_id
	$bound = 0
	foreach ($candidate in $candidates | Select-Object -First 15) {
		try {
			$breakpoint = Invoke-LiveRpc -OperationName 'set_breakpoint' -OperationArguments @{session_id=$sessionId;module=$plugin.filename;type=$candidate.declaring_type;method=$candidate.name}
			if ($breakpoint.bound) { $bound++ }
		} catch {}
	}
	if ($bound -eq 0) { throw 'No hot method breakpoint bound.' }
	$state = Invoke-LiveRpc -OperationName 'get_session_state' -OperationArguments @{session_id=$sessionId}
	if ($state.state -in @('paused','mixed')) { Invoke-LiveRpc -OperationName 'continue' -OperationArguments @{session_id=$sessionId;process_id=$processId} -DeadlineSeconds 30 | Out-Null }
	$wait = Invoke-LiveRpc -OperationName 'wait_for_stop' -OperationArguments @{session_id=$sessionId;after_event_id=$cursor;timeout_ms=12000} -DeadlineSeconds 30
	if ($wait.timed_out) { throw 'Timed out waiting for a hot UCH breakpoint.' }
	$stop = @($wait.events | Where-Object { $_.process_id -eq $processId } | Select-Object -First 1)[0]
	if (-not $stop) { throw 'The stop did not identify the selected UCH process.' }
	foreach ($activeProcessId in $processIds) {
		try { Invoke-LiveRpc -OperationName 'pause' -OperationArguments @{session_id=$sessionId;process_id=$activeProcessId} -DeadlineSeconds 30 | Out-Null } catch {
			if ($_.Exception.Message -notmatch 'already paused') { throw }
		}
	}
	$paused = Invoke-LiveRpc -OperationName 'get_session_state' -OperationArguments @{session_id=$sessionId}
	if ($paused.state -ne 'paused') { throw "Expected every UCH debug process to be paused, got '$($paused.state)'." }

	$registers = Invoke-LiveRpc -OperationName 'get_registers' -OperationArguments @{session_id=$sessionId;thread_id=$stop.thread_id;frame_index=0}
	$rip = @($registers.registers | Where-Object { $_.name -eq 'rip' })[0]
	if ($registers.architecture -ne 'x64' -or @($registers.registers).Count -ne 18 -or $rip.value -le 0 -or $rip.hex -notmatch '^0x[0-9A-F]{16}$') {
		throw "Invalid Mono register result: $($registers | ConvertTo-Json -Compress -Depth 5)"
	}
	Write-Host "PASS Mono get_registers: process=$processId thread=$($stop.thread_id) rip=$($rip.hex) rows=$(@($registers.registers).Count)"
}
finally {
	if ($sessionId) {
		try { Invoke-LiveRpc -OperationName 'clear_breakpoints' | Out-Null } catch {}
		foreach ($activeProcessId in $processIds) {
			try { Invoke-LiveRpc -OperationName 'continue' -OperationArguments @{session_id=$sessionId;process_id=$activeProcessId} -DeadlineSeconds 30 | Out-Null } catch {}
		}
		try { Invoke-LiveRpc -OperationName 'detach' -OperationArguments @{session_id=$sessionId} -DeadlineSeconds 30 | Out-Null } catch {}
	}
}
