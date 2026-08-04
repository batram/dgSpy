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

		# Close the remaining Phase 4 Mono gaps on a breakpoint in a method that has just proved live.
		# The settings checks are deterministic; the false-condition timeout followed by a conditioned
		# hit proves the engine evaluates the condition and hit count rather than merely storing them.
		Invoke-DgSpyRpc -OperationName 'clear_breakpoints'|Out-Null
		$liveBreakpoint=Invoke-DgSpyRpc -OperationName 'set_il_breakpoint' -OperationArguments @{session_id=$sessionId;module=$frame.module;method_token=$frame.method_token;il_offset=$frame.il_offset}
		$conditioned=Invoke-DgSpyRpc -OperationName 'update_breakpoint' -OperationArguments @{breakpoint_id=$liveBreakpoint.breakpoint_id;condition='false';condition_kind='is_true';hit_count=2;hit_count_kind='at_least'}
		Assert-That 'Mono stores a non-empty breakpoint condition and hit count' ($conditioned.condition -eq 'false' -and $conditioned.hit_count -eq 2 -and $conditioned.hit_count_kind -eq 'at_least')
		$tracepoint=Invoke-DgSpyRpc -OperationName 'update_breakpoint' -OperationArguments @{breakpoint_id=$liveBreakpoint.breakpoint_id;trace_message='dgspy-mono-trace';trace_continue=$true}
		Assert-That 'Mono stores a continuing tracepoint and warns that it produces no stop' ($tracepoint.trace_continue -and $tracepoint.warning -match 'no stopped event|wait_for_stop')
		Invoke-DgSpyRpc -OperationName 'update_breakpoint' -OperationArguments @{breakpoint_id=$liveBreakpoint.breakpoint_id;trace_message='';trace_continue=$false}|Out-Null
		$conditionCursor=(Invoke-DgSpyRpc -OperationName 'get_session_state' -OperationArguments @{session_id=$sessionId}).last_event_id
		Resume-IfPaused -SessionId $sessionId
		$falseWait=Invoke-DgSpyRpc -OperationName 'wait_for_stop' -OperationArguments @{session_id=$sessionId;after_event_id=$conditionCursor;timeout_ms=750} -DeadlineSeconds 10
		Assert-That 'a false Mono breakpoint condition suppresses the stop' ($falseWait.timed_out)
		$armed=Invoke-DgSpyRpc -OperationName 'update_breakpoint' -OperationArguments @{breakpoint_id=$liveBreakpoint.breakpoint_id;condition='true';condition_kind='is_true';hit_count=2;hit_count_kind='at_least'}
		$hitWait=Invoke-DgSpyRpc -OperationName 'wait_for_stop' -OperationArguments @{session_id=$sessionId;after_event_id=$falseWait.last_event_id;timeout_ms=12000} -DeadlineSeconds 30
		$hitStop=if(-not $hitWait.timed_out){@($hitWait.events)[0]}else{$null}
		Assert-That 'a true conditioned Mono breakpoint with a hit count actually stops' ($armed.bound -and $hitStop.stop_reason -eq 'breakpoint') "(timed_out=$($hitWait.timed_out))"
		if($hitStop){$thread=$hitStop.thread_id}

		# Exercise all three Mono step kinds. Disable the hot breakpoint so only step completion can stop.
		Invoke-DgSpyRpc -OperationName 'update_breakpoint' -OperationArguments @{breakpoint_id=$liveBreakpoint.breakpoint_id;enabled=$false}|Out-Null
		foreach($stepName in @('step_into','step_over','step_out')){
			$step=Try-Rpc $stepName @{session_id=$sessionId;thread_id=$thread} 30
			$stepWait=if(-not $step.dgspy_error){Invoke-DgSpyRpc -OperationName 'wait_for_stop' -OperationArguments @{session_id=$sessionId;after_event_id=$step.cursor_event_id;timeout_ms=12000} -DeadlineSeconds 30}else{$null}
			$stepStop=if($null -ne $stepWait -and -not $stepWait.timed_out){@($stepWait.events)[0]}else{$null}
			Assert-That "$stepName completes on Mono with stop reason step" ($null -eq $step.dgspy_error -and $stepStop.stop_reason -eq 'step') "(error=$($step.dgspy_error) timed_out=$($stepWait.timed_out))"
			if($stepStop){$thread=$stepStop.thread_id}
		}
		$stack=@(Invoke-DgSpyRpc -OperationName 'get_callstack' -OperationArguments @{session_id=$sessionId;thread_id=$thread;max_frames=10} -DeadlineSeconds 40|ForEach-Object{$_})
		$frame=$stack[0]

		# Close the remaining Phase 5 Mono gaps with a reversible process-global scalar and watches.
		$mutableExpression='UnityExplorer.InspectorManager.PanelWidth'
		$oldPanelWidth=Try-Rpc 'evaluate' @{session_id=$sessionId;expression=$mutableExpression;thread_id=$thread;frame_index=0} 40
		$expectedPanelWidth=([double]$oldPanelWidth.value)+1
		$newPanelWidth=([Convert]::ToString($expectedPanelWidth,[Globalization.CultureInfo]::InvariantCulture)+'F')
		$assigned=Try-Rpc 'set_value' @{session_id=$sessionId;expression=$mutableExpression;value=$newPanelWidth;thread_id=$thread;frame_index=0} 40
		$assignedRead=Try-Rpc 'evaluate' @{session_id=$sessionId;expression=$mutableExpression;thread_id=$thread;frame_index=0} 40
		Assert-That 'set_value assigns and reads back a Mono value' ($assigned.assigned -and [double]$assignedRead.value -eq $expectedPanelWidth) "(rpc_error=$($assigned.dgspy_error) assignment_error=$($assigned.error))"
		$restoreValue=([Convert]::ToString([double]$oldPanelWidth.value,[Globalization.CultureInfo]::InvariantCulture)+'F')
		Try-Rpc 'set_value' @{session_id=$sessionId;expression=$mutableExpression;value=$restoreValue;thread_id=$thread;frame_index=0} 40|Out-Null
		$watch=Invoke-DgSpyRpc -OperationName 'add_watch' -OperationArguments @{session_id=$sessionId;expression=$mutableExpression}
		$brokenWatch=Invoke-DgSpyRpc -OperationName 'add_watch' -OperationArguments @{session_id=$sessionId;expression='dgspy_no_such_symbol'}
		$watches=@(Invoke-DgSpyRpc -OperationName 'list_watches' -OperationArguments @{session_id=$sessionId;thread_id=$thread;frame_index=0}|ForEach-Object{$_})
		$goodWatch=$watches|Where-Object{$_.watch_id -eq $watch.watch_id}|Select-Object -First 1
		$failedWatch=$watches|Where-Object{$_.watch_id -eq $brokenWatch.watch_id}|Select-Object -First 1
		Assert-That 'Mono watches evaluate valid entries without failing the invalid entry' ($null -eq $goodWatch.value.error -and -not [string]::IsNullOrWhiteSpace($failedWatch.value.error)) "(good=$($goodWatch.value.error) bad=$($failedWatch.value.error))"
		$removedWatch=Invoke-DgSpyRpc -OperationName 'remove_watch' -OperationArguments @{session_id=$sessionId;watch_id=$watch.watch_id}
		Invoke-DgSpyRpc -OperationName 'remove_watch' -OperationArguments @{session_id=$sessionId;watch_id=$brokenWatch.watch_id}|Out-Null
		Assert-That 'remove_watch removes the selected Mono watch' ($removedWatch.watch_id -eq $watch.watch_id)

		# Phase 7 advertises these capabilities for Mono. Use an idempotent memory write and the current
		# instruction pointer so the validation does not deliberately alter game behavior.
		$invoked=Try-Rpc 'invoke_method' @{session_id=$sessionId;expression='System.Math.Abs(-7)';thread_id=$thread;frame_index=0;timeout_ms=3000} 40
		Assert-That 'invoke_method performs audited Mono func-eval' ($invoked.completed -and $invoked.value.value -eq 7 -and -not [string]::IsNullOrWhiteSpace($invoked.audit_id)) "($($invoked.dgspy_error))"
		$created=Try-Rpc 'create_object' @{session_id=$sessionId;expression='new System.Text.StringBuilder()';thread_id=$thread;frame_index=0;timeout_ms=3000} 40
		Assert-That 'create_object performs audited Mono construction' ($created.completed -and $created.capability -eq 'object_construction' -and -not [string]::IsNullOrWhiteSpace($created.audit_id)) "($($created.dgspy_error))"
		$moduleAddress=[uint64](Get-Process -Id $plugin.process_id).MainModule.BaseAddress.ToInt64()
		$memory=Try-Rpc 'read_memory' @{session_id=$sessionId;process_id=$plugin.process_id;address=$moduleAddress;length=2}
		$memoryBytes=if($memory.data_base64){[Convert]::FromBase64String($memory.data_base64)}else{@()}
		Assert-That 'read_memory reads bounded Mono process bytes' ($memoryBytes.Count -eq 2 -and $memoryBytes[0] -eq 0x4D -and $memoryBytes[1] -eq 0x5A) "($($memory.dgspy_error))"
		$written=Try-Rpc 'write_memory' @{session_id=$sessionId;process_id=$plugin.process_id;address=$moduleAddress;data_base64=$memory.data_base64}
		Assert-That 'write_memory reports an idempotent Mono write as side effecting' ($written.written -and $written.causes_side_effects) "($($written.dgspy_error))"
		$managedDisassembly=Try-Rpc 'get_disassembly' @{session_id=$sessionId;mode='managed';module=$frame.module;method_token=$frame.method_token} 40
		Assert-That 'get_disassembly exposes managed IL on Mono' ($managedDisassembly.capability -eq 'managed_il' -and @($managedDisassembly.body.instructions).Count -gt 0) "($($managedDisassembly.dgspy_error))"
		$nativeDisassembly=Try-Rpc 'get_disassembly' @{session_id=$sessionId;mode='native';thread_id=$thread;frame_index=0} 40
		Assert-That 'Mono native disassembly returns the advertised capability failure' ($nativeDisassembly.dgspy_error -match 'capability_unsupported|not supported') "($($nativeDisassembly.dgspy_error))"
		$registers=Try-Rpc 'get_registers' @{session_id=$sessionId;thread_id=$thread}
		Assert-That 'get_registers returns the host-wide capability failure on Mono' ($registers.dgspy_error -match 'capability_unsupported|not exposed') "($($registers.dgspy_error))"
		$currentFrame=Try-Rpc 'get_frame' @{session_id=$sessionId;thread_id=$thread;frame_index=0} 40
		$setIp=Try-Rpc 'set_instruction_pointer' @{session_id=$sessionId;thread_id=$thread;frame_index=0;module=$currentFrame.module;method_token=$currentFrame.method_token;il_offset=$currentFrame.il_offset} 40
		Assert-That 'set_instruction_pointer validates and audits the current Mono location' ($setIp.completed -and $setIp.capability -eq 'set_instruction_pointer' -and -not [string]::IsNullOrWhiteSpace($setIp.audit_id)) "($($setIp.dgspy_error))"
		# The original Phase 8 object-ID check resumes and waits for the next hot-method stop. Restore the
		# breakpoint that the Phase 4 step checks deliberately disabled before entering that existing path.
		Invoke-DgSpyRpc -OperationName 'update_breakpoint' -OperationArguments @{breakpoint_id=$liveBreakpoint.breakpoint_id;enabled=$true;condition='';hit_count=1;hit_count_kind='at_least'}|Out-Null

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
