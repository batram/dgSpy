param(
	[int]$BridgePort = 7311,
	[string]$ReparseExportPath
)

$ErrorActionPreference = 'Stop'
$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new()
. (Join-Path (Split-Path $PSScriptRoot) 'ps_scratch\Invoke-DgSpyRpc.ps1')

$script:checks=0; $script:failures=@()
function Assert-That { param([string]$What,$Condition,[string]$Detail='') $script:checks++; if(@($Condition).Count -gt 0 -and [bool](@($Condition)|Select-Object -Last 1)){ Write-Host "  PASS  $What" -ForegroundColor DarkGreen } else { Write-Host "  FAIL  $What $Detail" -ForegroundColor Red; $script:failures += "$What $Detail" } }
function Try-Rpc { param([string]$OperationName,[hashtable]$OperationArguments=@{},[int]$DeadlineSeconds=40) try { Invoke-DgSpyRpc -OperationName $OperationName -OperationArguments $OperationArguments -DeadlineSeconds $DeadlineSeconds } catch { [pscustomobject]@{ dgspy_error=$_.Exception.Message } } }
function Resume-IfPaused { param([string]$SessionId) $state=Invoke-DgSpyRpc -OperationName 'get_session_state' -OperationArguments @{session_id=$SessionId};if($state.state -eq 'paused'){Invoke-DgSpyRpc -OperationName 'continue' -OperationArguments @{session_id=$SessionId} -DeadlineSeconds 30|Out-Null} }
function Start-BridgeExecution {
	param([Parameter(Mandatory=$true)][string]$Code)
	$uri="http://127.0.0.1:$BridgePort/execute"
	Start-Job -ScriptBlock { param([string]$TargetUri,[string]$SourceCode) Invoke-RestMethod -Method Post -Uri $TargetUri -Body $SourceCode -ContentType 'text/plain' -TimeoutSec 45 } -ArgumentList $uri,$Code
}

$sessionId=$null
$jobs=@()
$cleanupObject=$null
$hostExportPath=$null
try {
	$attached=Invoke-DgSpyRpc -OperationName 'attach_endpoint' -OperationArguments @{ address='127.0.0.1';port=55555;engine='unity';process_is_suspended=$false;connection_timeout_ms=10000 } -DeadlineSeconds 30
	$sessionId=$attached.session_id
	Assert-That 'Phase 8 pass attaches to live UCH' ($attached.state -in @('running','paused')) "(state=$($attached.state) fault='$($attached.fault_message)')"
	$capabilities=Invoke-DgSpyRpc -OperationName 'get_capabilities'
	$mono=@($capabilities.engines|Where-Object{$_.engine -eq 'unity'})[0]
	Assert-That 'Mono capabilities advertise Phase 8 object IDs and exception modes' ($mono.object_ids -and @($mono.exception_modes).Count -gt 0)

	$modules=@(Invoke-DgSpyRpc -OperationName 'list_modules' -OperationArguments @{session_id=$sessionId}|ForEach-Object{$_})
	$plugin=$modules|Where-Object{$_.filename -like '*UltimateGlorpExplorer*'}|Select-Object -First 1
	Assert-That 'UCH exposes a file-backed plugin module for analysis and breakpoints' ($null -ne $plugin -and $plugin.can_set_breakpoint)
	$search=Invoke-DgSpyRpc -OperationName 'search_symbols' -OperationArguments @{session_id=$sessionId;pattern='Update';module=$plugin.filename;kinds=@('method');count=40} -DeadlineSeconds 60
	$candidates=@($search.symbols)
	Assert-That 'symbol search finds hot managed UCH methods' ($candidates.Count -gt 0)

	Invoke-DgSpyRpc -OperationName 'clear_breakpoints' | Out-Null
	$cursor=(Invoke-DgSpyRpc -OperationName 'get_session_state' -OperationArguments @{session_id=$sessionId}).last_event_id
	$bound=0
	foreach($candidate in $candidates|Select-Object -First 15){ $bp=Try-Rpc 'set_breakpoint' @{session_id=$sessionId;module=$plugin.filename;type=$candidate.declaring_type;method=$candidate.name}; if(-not $bp.dgspy_error -and $bp.bound){$bound++} }
	if((Invoke-DgSpyRpc -OperationName 'get_session_state' -OperationArguments @{session_id=$sessionId}).state -eq 'paused'){ Invoke-DgSpyRpc -OperationName 'continue' -OperationArguments @{session_id=$sessionId} -DeadlineSeconds 30|Out-Null }
	$wait=Invoke-DgSpyRpc -OperationName 'wait_for_stop' -OperationArguments @{session_id=$sessionId;after_event_id=$cursor;timeout_ms=12000} -DeadlineSeconds 30
	$stop=if(-not $wait.timed_out){@($wait.events)[0]}else{$null}
	Assert-That 'a hot UCH method reaches a managed Mono stop' ($bound -gt 0 -and $null -ne $stop) "(bound=$bound timed_out=$($wait.timed_out))"

	if($stop){
		$thread=$stop.thread_id
		$stack=@(Invoke-DgSpyRpc -OperationName 'get_callstack' -OperationArguments @{session_id=$sessionId;thread_id=$thread;max_frames=10} -DeadlineSeconds 40|ForEach-Object{$_})
		$frame=$stack[0]
		Assert-That 'the Phase 8 stop has a managed frame identity' ($frame.method_token -gt 0 -and -not [string]::IsNullOrWhiteSpace($frame.module))

		$autos=Try-Rpc 'get_autos' @{session_id=$sessionId;process_id=$plugin.process_id;runtime_id=$plugin.runtime_guid;thread_id=$thread;frame_index=0} 40
		Assert-That 'get_autos reaches the Mono C# provider' ($null -eq $autos.dgspy_error -and @($autos).Count -gt 0) "($($autos.dgspy_error))"
		$object=Try-Rpc 'create_object_id' @{session_id=$sessionId;expression='System.AppDomain.CurrentDomain';allow_func_eval=$true;process_id=$plugin.process_id;runtime_id=$plugin.runtime_guid;thread_id=$thread;frame_index=0} 40
		Assert-That 'create_object_id works on a Mono object reference' ($null -eq $object.dgspy_error -and $object.object_id -gt 0) "($($object.dgspy_error))"
		if($object.object_id){
			$idCursor=(Invoke-DgSpyRpc -OperationName 'get_session_state' -OperationArguments @{session_id=$sessionId}).last_event_id
			Invoke-DgSpyRpc -OperationName 'continue' -OperationArguments @{session_id=$sessionId} -DeadlineSeconds 30|Out-Null
			$idWait=Invoke-DgSpyRpc -OperationName 'wait_for_stop' -OperationArguments @{session_id=$sessionId;after_event_id=$idCursor;timeout_ms=12000} -DeadlineSeconds 30
			$idStop=if(-not $idWait.timed_out){@($idWait.events)[0]}else{$null}
			Assert-That 'the Mono object ID remains retained across resume' ($null -ne $idStop) "(timed_out=$($idWait.timed_out))"
			if($idStop){ $thread=$idStop.thread_id; $stack=@(Invoke-DgSpyRpc -OperationName 'get_callstack' -OperationArguments @{session_id=$sessionId;thread_id=$thread;max_frames=10} -DeadlineSeconds 40|ForEach-Object{$_}); $frame=$stack[0] }
			$read=Try-Rpc 'evaluate_object_id' @{session_id=$sessionId;object_id=$object.object_id;process_id=$plugin.process_id;runtime_id=$plugin.runtime_guid;thread_id=$thread;frame_index=0} 40
			Assert-That 'evaluate_object_id resolves the Mono reference' ($null -eq $read.dgspy_error -and $null -eq $read.value.error) "($($read.dgspy_error))"
			$released=Try-Rpc 'release_object_id' @{session_id=$sessionId;object_id=$object.object_id;process_id=$plugin.process_id;runtime_id=$plugin.runtime_guid}
			Assert-That 'release_object_id releases the Mono reference' ($released.object_id -eq $object.object_id) "($($released.dgspy_error))"
		}
		$cleanupObject=Try-Rpc 'create_object_id' @{session_id=$sessionId;expression='System.AppDomain.CurrentDomain';allow_func_eval=$true;process_id=$plugin.process_id;runtime_id=$plugin.runtime_guid;thread_id=$thread;frame_index=0} 40
		Assert-That 'a second Mono object ID is retained for detach cleanup verification' ($null -eq $cleanupObject.dgspy_error -and $cleanupObject.object_id -gt 0) "($($cleanupObject.dgspy_error))"

		$value=Try-Rpc 'get_value_export' @{session_id=$sessionId;expression='1 + 1';process_id=$plugin.process_id;runtime_id=$plugin.runtime_guid;thread_id=$thread;frame_index=0;count=4} 40
		Assert-That 'get_value_export returns hashed Mono scalar bytes' ($null -eq $value.dgspy_error -and $value.total_size -eq 4 -and $value.sha256.Length -eq 64) "($($value.dgspy_error))"
		if(-not [string]::IsNullOrWhiteSpace($ReparseExportPath)){
			$exportName="mono-value-$([Guid]::NewGuid().ToString('N')).bin"
			$hostExport=Try-Rpc 'write_value_export' @{session_id=$sessionId;expression='1 + 1';path=$exportName;process_id=$plugin.process_id;runtime_id=$plugin.runtime_guid;thread_id=$thread;frame_index=0} 40
			$hostExportPath=$hostExport.path
			Assert-That 'host value export writes hashed Mono bytes below the configured root' ($null -eq $hostExport.dgspy_error -and (Test-Path -LiteralPath $hostExport.path) -and $hostExport.sha256 -eq $value.sha256) "($($hostExport.dgspy_error))"
			$reparse=Try-Rpc 'write_value_export' @{session_id=$sessionId;expression='1 + 1';path=$ReparseExportPath;process_id=$plugin.process_id;runtime_id=$plugin.runtime_guid;thread_id=$thread;frame_index=0} 40
			Assert-That 'host export refuses a path crossing a reparse point' ($reparse.dgspy_error -match 'reparse point') "($($reparse.dgspy_error))"
		}
		$analysis=Try-Rpc 'analyze_symbol' @{session_id=$sessionId;module=$frame.module;token=$frame.method_token;search_module=$plugin.name;count=20;max_methods=200} 60
		Assert-That 'analyze_symbol runs bounded analysis on Mono metadata' ($null -eq $analysis.dgspy_error -and $analysis.scanned_methods -le 200) "($($analysis.dgspy_error))"
	}

	$moduleBp=Invoke-DgSpyRpc -OperationName 'set_module_breakpoint' -OperationArguments @{session_id=$sessionId;module_name='Phase8.UCH.NeverLoaded';is_loaded=$true;is_in_memory=$true}
	$moduleBps=@(Invoke-DgSpyRpc -OperationName 'list_module_breakpoints' -OperationArguments @{session_id=$sessionId}|ForEach-Object{$_})
	Assert-That 'module breakpoint filters round-trip on the Mono session' (@($moduleBps|Where-Object{$_.breakpoint_id -eq $moduleBp.breakpoint_id -and $_.is_loaded -and $_.is_in_memory}).Count -eq 1)
	Invoke-DgSpyRpc -OperationName 'remove_module_breakpoint' -OperationArguments @{session_id=$sessionId;breakpoint_id=$moduleBp.breakpoint_id}|Out-Null
	$document=Invoke-DgSpyRpc -OperationName 'export_breakpoints' -OperationArguments @{session_id=$sessionId;max_exception_policies=5000} -DeadlineSeconds 60
	$dry=Invoke-DgSpyRpc -OperationName 'import_breakpoints' -OperationArguments @{session_id=$sessionId;document=$document;mode='merge';dry_run=$true} -DeadlineSeconds 60
	Assert-That 'breakpoint export/import dry-run succeeds on Mono' ($dry.dry_run -and $dry.removed -eq 0 -and -not $document.exception_truncated)

	$policy=Invoke-DgSpyRpc -OperationName 'set_exception_policy' -OperationArguments @{session_id=$sessionId;category='DotNet';name='UCH.Phase8FixtureException';stop_thrown=$true;conditions=@(@{kind='module_equals';module='Assembly-CSharp.dll'})}
	Assert-That 'exception flags and conditions round-trip on Mono' ($policy.stop_thrown -and @($policy.conditions).Count -eq 1)
	$removed=Invoke-DgSpyRpc -OperationName 'remove_exception_policy' -OperationArguments @{session_id=$sessionId;category='DotNet';name='UCH.Phase8FixtureException'}
	Assert-That 'custom Mono exception policy can be removed' ($removed.name -eq 'UCH.Phase8FixtureException')
	$output=Invoke-DgSpyRpc -OperationName 'get_output' -OperationArguments @{session_id=$sessionId;after_output_id=0}
	Assert-That 'debugger output is available separately from stop events on Mono' ($output.last_output_id -ge @($output.messages).Count)

	# The hot-method breakpoints above are only a way to obtain a managed frame. Leaving them armed makes
	# the game stop again before its main-thread bridge can execute the controlled exception/unload triggers.
	Invoke-DgSpyRpc -OperationName 'clear_breakpoints'|Out-Null
	Resume-IfPaused -SessionId $sessionId
	$livePolicy=Invoke-DgSpyRpc -OperationName 'set_exception_policy' -OperationArguments @{session_id=$sessionId;category='DotNet';name='System.FormatException';stop_thrown=$true}
	$exceptionCursor=(Invoke-DgSpyRpc -OperationName 'get_session_state' -OperationArguments @{session_id=$sessionId}).last_event_id
	$exceptionJob=Start-BridgeExecution -Code 'int.Parse("dgspy-phase8-exception")'
	$jobs+=$exceptionJob
	$exceptionWait=Invoke-DgSpyRpc -OperationName 'wait_for_stop' -OperationArguments @{session_id=$sessionId;after_event_id=$exceptionCursor;timeout_ms=12000} -DeadlineSeconds 30
	$exceptionStop=if(-not $exceptionWait.timed_out){@($exceptionWait.events)[0]}else{$null}
	Assert-That 'a categorized Mono exception policy causes an actual first-chance stop' ($exceptionStop.stop_reason -eq 'exception' -and $exceptionStop.exception_id -eq 'DotNet - System.FormatException' -and $exceptionStop.exception_first_chance) "(timed_out=$($exceptionWait.timed_out) reason=$($exceptionStop.stop_reason) id=$($exceptionStop.exception_id))"
	if($exceptionStop){$exceptionValues=@(Invoke-DgSpyRpc -OperationName 'get_exception' -OperationArguments @{session_id=$sessionId;thread_id=$exceptionStop.thread_id;frame_index=0} -DeadlineSeconds 30|ForEach-Object{$_});Assert-That 'get_exception exposes the stopped Mono FormatException' (($exceptionValues|ConvertTo-Json -Compress -Depth 5)-match 'FormatException')}
	Resume-IfPaused -SessionId $sessionId
	Wait-Job -Job $exceptionJob -Timeout 20|Out-Null
	Receive-Job -Job $exceptionJob -ErrorAction SilentlyContinue|Out-Null
	Invoke-DgSpyRpc -OperationName 'remove_exception_policy' -OperationArguments @{session_id=$sessionId;category='DotNet';name='System.FormatException'}|Out-Null

	$unloadBreakpoint=Invoke-DgSpyRpc -OperationName 'set_module_breakpoint' -OperationArguments @{session_id=$sessionId;module_name='mscorlib*';is_loaded=$false}
	$unloadCursor=(Invoke-DgSpyRpc -OperationName 'get_session_state' -OperationArguments @{session_id=$sessionId}).last_event_id
	# CreateDomain alone stays lazy on this Unity Mono build. Loading a real file-backed assembly initializes
	# the domain and makes its mscorlib unload observable when AppDomain.Unload completes.
	$escapedPlugin=$plugin.filename.Replace('"','""')
	$unloadCode="var domain = System.AppDomain.CreateDomain(`"dgspy-phase8-unload`"); domain.Load(System.Reflection.AssemblyName.GetAssemblyName(@`"$escapedPlugin`")); System.AppDomain.Unload(domain); `"unloaded`""
	$unloadJob=Start-BridgeExecution -Code $unloadCode
	$jobs+=$unloadJob
	$unloadWait=Invoke-DgSpyRpc -OperationName 'wait_for_stop' -OperationArguments @{session_id=$sessionId;after_event_id=$unloadCursor;timeout_ms=12000} -DeadlineSeconds 30
	$unloadEvents=@((Invoke-DgSpyRpc -OperationName 'get_events' -OperationArguments @{session_id=$sessionId;after_event_id=$unloadCursor}).events|ForEach-Object{$_})
	$moduleUnloaded=@($unloadEvents|Where-Object{$_.kind -eq 'module_unloaded' -and $_.module -like '*mscorlib.dll'}|Select-Object -Last 1)
	$unloadStop=if(-not $unloadWait.timed_out){@($unloadWait.events)[0]}else{$null}
	Assert-That 'a Mono module-unload breakpoint causes an actual stop' ($moduleUnloaded.Count -eq 1 -and $null -ne $unloadStop -and $moduleUnloaded[0].event_id -lt $unloadStop.event_id) "(timed_out=$($unloadWait.timed_out) unload_events=$($moduleUnloaded.Count))"
	Resume-IfPaused -SessionId $sessionId
	Wait-Job -Job $unloadJob -Timeout 20|Out-Null
	$unloadResult=Receive-Job -Job $unloadJob -ErrorAction SilentlyContinue
	Assert-That 'the temporary Mono AppDomain unload completes' ($unloadJob.State -eq 'Completed' -and $unloadResult.ok -and $unloadResult.result -eq 'unloaded') "(state=$($unloadJob.State) error=$($unloadResult.error))"
	Invoke-DgSpyRpc -OperationName 'remove_module_breakpoint' -OperationArguments @{session_id=$sessionId;breakpoint_id=$unloadBreakpoint.breakpoint_id}|Out-Null

	if($cleanupObject.object_id){
		Invoke-DgSpyRpc -OperationName 'clear_breakpoints'|Out-Null
		Resume-IfPaused -SessionId $sessionId
		$cleanupId=$cleanupObject.object_id
		$detached=Invoke-DgSpyRpc -OperationName 'detach' -OperationArguments @{session_id=$sessionId} -DeadlineSeconds 30
		Assert-That 'cleanup verification detaches without terminating UCH' ($detached.detached -and -not $detached.terminated)
		$sessionId=$null
		$reattached=Invoke-DgSpyRpc -OperationName 'attach_endpoint' -OperationArguments @{address='127.0.0.1';port=55555;engine='unity';process_is_suspended=$false;connection_timeout_ms=10000} -DeadlineSeconds 30
		$sessionId=$reattached.session_id
		$reattachModules=@(Invoke-DgSpyRpc -OperationName 'list_modules' -OperationArguments @{session_id=$sessionId}|ForEach-Object{$_})
		$reattachPlugin=$reattachModules|Where-Object{$_.filename -like '*UltimateGlorpExplorer*'}|Select-Object -First 1
		$idsAfterDetach=@(Invoke-DgSpyRpc -OperationName 'list_object_ids' -OperationArguments @{session_id=$sessionId;process_id=$reattachPlugin.process_id;runtime_id=$reattachPlugin.runtime_guid}|ForEach-Object{$_})
		Assert-That 'detach disposes Mono runtime-scoped object IDs before reattach' (@($idsAfterDetach|Where-Object{$_.object_id -eq $cleanupId}).Count -eq 0)
	}
} finally {
	foreach($job in $jobs){Remove-Job -Job $job -Force -ErrorAction SilentlyContinue}
	if($sessionId){
		try{Invoke-DgSpyRpc -OperationName 'clear_breakpoints'|Out-Null}catch{}
		try{$state=Invoke-DgSpyRpc -OperationName 'get_session_state' -OperationArguments @{session_id=$sessionId};if($state.state -eq 'paused'){Invoke-DgSpyRpc -OperationName 'continue' -OperationArguments @{session_id=$sessionId} -DeadlineSeconds 30|Out-Null}}catch{}
		try{$detached=Invoke-DgSpyRpc -OperationName 'detach' -OperationArguments @{session_id=$sessionId} -DeadlineSeconds 30;Assert-That 'Phase 8 pass detaches without terminating UCH' ($detached.detached -and -not $detached.terminated)}catch{$script:failures+='detach failed'}
	}
	# dnSpy may run as the normal Windows identity while this harness is sandboxed. Cleanup is best-effort
	# and deliberately happens after safe detach so an ACL mismatch can never strand an attached game.
	if($hostExportPath -and (Test-Path -LiteralPath $hostExportPath)){try{Remove-Item -LiteralPath $hostExportPath -Force}catch{Write-Warning "Could not remove temporary host export '$hostExportPath': $($_.Exception.Message)"}}
	Assert-That 'UCH remains alive after the Phase 8 pass' (@(Get-Process UltimateChickenHorse -ErrorAction SilentlyContinue).Count -gt 0)
	if($script:failures.Count -eq 0){Write-Host "PASSED  $($script:checks) checks" -ForegroundColor Green}else{Write-Host "FAILED  $($script:failures.Count) of $($script:checks) checks" -ForegroundColor Red;$script:failures|ForEach-Object{Write-Host "  - $_" -ForegroundColor Red};exit 1}
}
