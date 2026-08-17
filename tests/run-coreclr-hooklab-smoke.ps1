#requires -Version 5.1
<# Dedicated CoreCLR HookLab backend lifecycle smoke. ASCII-only; run on a hidden desktop. #>
[CmdletBinding()]
param([int]$RpcPort = 0,[string]$RunDirectory)
$ErrorActionPreference = 'Stop'
$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new()
$repoRoot = Split-Path $PSScriptRoot -Parent
$targetExe = Join-Path $repoRoot 'tests\TestTargets\CoreClrDebuggerTarget\bin\Release\net10.0\CoreClrDebuggerTarget.exe'
$root = if ($RunDirectory) { $RunDirectory } else { Join-Path $PSScriptRoot ('artifacts\coreclr-hooklab-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
$out = Join-Path $root 'target.out'; $hostId=0; $target=$null; $sessionId=$null; $pass=0; $fail=0
. (Join-Path $PSScriptRoot 'TestSupport\Invoke-DgSpyRpc.ps1')
. (Join-Path $PSScriptRoot 'TestSupport\Find-DgSpyProgram.ps1')
function Rpc([string]$Operation,[hashtable]$Arguments=@{},[int]$Deadline=70) { Invoke-DgSpyRpc -OperationName $Operation -OperationArguments $Arguments -RpcPort $RpcPort -DeadlineSeconds $Deadline }
function Check([string]$Name,$Condition,[string]$Detail='') { if([bool]$Condition){$script:pass++;Write-Host "PASS  $Name" -ForegroundColor DarkGreen}else{$script:fail++;Write-Host "FAIL  $Name  $Detail" -ForegroundColor Red} }
function Lines { @(Get-Content -LiteralPath $out -ErrorAction SilentlyContinue) }
function Wait-Line([string]$Text,[int]$Seconds=15) { $deadline=[DateTime]::UtcNow.AddSeconds($Seconds);do{Start-Sleep -Milliseconds 200;if(@(Lines|Where-Object{$_ -eq $Text}).Count){return $true}}until([DateTime]::UtcNow -gt $deadline);return $false }
function Fact([string]$Name) { ((Lines|Where-Object{$_ -like "$Name *"}|Select-Object -First 1) -replace ('^'+$Name+' '),'') }
try {
	New-Item -ItemType Directory -Force -Path $root|Out-Null; Start-Transcript -LiteralPath (Join-Path $root 'driver.log') -Force|Out-Null
	$target=Start-Process -FilePath $targetExe -WindowStyle Hidden -PassThru -RedirectStandardOutput $out -RedirectStandardError (Join-Path $root 'target.err')
	if(-not(Wait-Line 'DELTA 7')){throw 'CoreCLR fixture did not publish baseline delta 7.'}
	$hostId=& (Join-Path $PSScriptRoot 'TestSupport\Start-DgSpyHost.ps1') -RpcPort $RpcPort; $RpcPort=[int]$env:DGSPY_RPC_PORT
	$program=Find-DgSpyProgram -ProcessId $target.Id -InvokeRpc ${function:Rpc}; $attached=Rpc 'attach' @{program_id=$program.program_id}; $sessionId=$attached.session_id
	if($attached.state -eq 'paused'){$null=Rpc 'continue' @{session_id=$sessionId}}
	$beforeCount=@(Lines).Count; $progressDeadline=[DateTime]::UtcNow.AddSeconds(10);do{Start-Sleep -Milliseconds 200}until(@(Lines).Count -gt $beforeCount -or [DateTime]::UtcNow -gt $progressDeadline)
	Check 'CoreCLR target is running before initialization' (@(Lines).Count -gt $beforeCount)
	$modulePage=Rpc 'list_modules' @{session_id=$sessionId;name_pattern='CoreClrDebuggerTarget'}; $modules=@($modulePage.modules); $mvid=Fact 'MVID'; $module=@($modules|Where-Object{$_.filename -like '*CoreClrDebuggerTarget*'})
	Check 'discovery returns one session-scoped CoreCLR module ID' ($module.Count -eq 1 -and $module[0].module_id) ("count="+$module.Count)
	$initialized=Rpc 'initialize_hooklab' @{session_id=$sessionId;process_id=$target.Id} 120
	Check 'CoreCLR backend initializes an authenticated resident' ($initialized.initialized -and $initialized.state -eq 'ready' -and $initialized.probe_instance_id) ($initialized|ConvertTo-Json -Compress)
	$status=Rpc 'get_hooklab_status' @{session_id=$sessionId;process_id=$target.Id}; Check 'resident status reads back ready' ($status.initialized -and $status.runtimes.Count -eq 1)
	$base=@{session_id=$sessionId;process_id=$target.Id;hook_id='coreclr-probe';kind='Postfix';module_id=$module[0].module_id;assembly='CoreClrDebuggerTarget';declaring_type='CoreClrDebuggerTarget.Program';method='Probe';method_token=[int](Fact 'TOKEN');signature=(Fact 'SIGNATURE');module_mvid=$mvid;il_sha256=(Fact 'ILSHA');revision=1;source='public static class CoreClrPostfixV1 { public static void Postfix(ref int __result) { __result += 100; } }'}
	$created=Rpc 'create_hook' $base; Check 'compiled revision 1 installs' ($created.installed -and $created.hook.revision -eq 1); Check 'revision 1 changes live behavior' (Wait-Line 'DELTA 107')
	$base.revision=2; $base.source='public static class CoreClrPostfixV2 { public static void Postfix(ref int __result) { __result += 200; } }'; $updated=Rpc 'update_hook' $base
	Check 'compiled revision 2 atomically replaces revision 1' ($updated.installed -and $updated.hook.revision -eq 2); Check 'revision 2 changes live behavior' (Wait-Line 'DELTA 207')
	$disabled=Rpc 'disable_hook' @{session_id=$sessionId;process_id=$target.Id;hook_id='coreclr-probe'}; Check 'disable reports disabled' (-not $disabled.hook.enabled); Check 'disable restores original behavior' (Wait-Line 'DELTA 7')
	$enabled=Rpc 'enable_hook' @{session_id=$sessionId;process_id=$target.Id;hook_id='coreclr-probe'}; Check 'enable reports enabled' $enabled.hook.enabled; Check 'enable restores revision 2' (Wait-Line 'DELTA 207')
	$removed=Rpc 'remove_hook' @{session_id=$sessionId;process_id=$target.Id;hook_id='coreclr-probe'}; Check 'remove reports ownership-scoped removal' $removed.removed; Check 'remove restores original behavior' (Wait-Line 'DELTA 7')
	$hooks=Rpc 'list_hooks' @{session_id=$sessionId;process_id=$target.Id}; Check 'resident inventory is empty after remove' ($hooks.hooks.Count -eq 0)
}
finally { if($sessionId){try{$null=Rpc 'detach' @{session_id=$sessionId} 30}catch{}};foreach($id in @($hostId,$(if($target){$target.Id}else{0}))){if($id -gt 0){Stop-Process -Id $id -Force -ErrorAction SilentlyContinue}};Write-Host "CoreCLR HookLab smoke: $pass passed, $fail failed" -ForegroundColor Cyan;Write-Host "logs: $root";try{Stop-Transcript|Out-Null}catch{} }
if($fail -ne 0){exit 1}
