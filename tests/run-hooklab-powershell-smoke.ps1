#requires -Version 5.1
[CmdletBinding()]
param([string]$RunDirectory)

$ErrorActionPreference = 'Stop'
$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new()
$repoRoot = Split-Path $PSScriptRoot -Parent
$fixtureDll = Join-Path $repoRoot 'tests\TestTargets\HookLabPowerShellFixture\bin\Release\net48\HookLabPowerShellFixture.dll'
if ([string]::IsNullOrWhiteSpace($RunDirectory)) { $RunDirectory = Join-Path $PSScriptRoot 'artifacts\hooklab-powershell' }
New-Item -ItemType Directory -Force -Path $RunDirectory | Out-Null
$statusPath = Join-Path $RunDirectory 'status.log'
$observedPath = Join-Path $RunDirectory 'observed.txt'
$instanceObservedPath = Join-Path $RunDirectory 'instance-observed.txt'
$finalizerObservedPath = Join-Path $RunDirectory 'finalizer-observed.txt'
$stopPath = Join-Path $RunDirectory 'stop'
$childScript = Join-Path $RunDirectory 'child.ps1'
$hostProcessId = 0
$child = $null
$sessionId = $null
$rpcPort = 0
. (Join-Path $PSScriptRoot 'TestSupport\Invoke-DgSpyRpc.ps1')

function Status([string]$Text) {
	$line = (Get-Date -Format 'HH:mm:ss.fff') + ' ' + $Text
	Write-Host $line
	Add-Content -LiteralPath $statusPath -Value $line -Encoding utf8
}
function Rpc([string]$Operation,[hashtable]$Arguments=@{},[int]$Deadline=30) {
	Invoke-DgSpyRpc -OperationName $Operation -OperationArguments $Arguments -RpcPort $rpcPort -DeadlineSeconds $Deadline
}
function Wait-Observed([int]$Expected,[string]$Path=$observedPath,[int]$Seconds=10) {
	$deadline=[DateTime]::UtcNow.AddSeconds($Seconds)
	do { if(Test-Path -LiteralPath $Path) { $value=[int](Get-Content -LiteralPath $Path -Raw); if($value -eq $Expected) { return $true } }; Start-Sleep -Milliseconds 100 } while([DateTime]::UtcNow -lt $deadline)
	return $false
}
function Wait-Text([string]$Expected,[string]$Path,[int]$Seconds=10) { $deadline=[DateTime]::UtcNow.AddSeconds($Seconds); do { if((Test-Path $Path)-and(Get-Content $Path -Raw)-eq$Expected){return $true};Start-Sleep -Milliseconds 100 }while([DateTime]::UtcNow-lt$deadline);return $false }
function Facts([string]$Path,[string]$TypeName,[string]$Signature,[string]$MethodName='Calculate') {
	$assembly=[Reflection.Assembly]::LoadFrom($Path); $method=$assembly.GetType($TypeName,$true).GetMethod($MethodName)
	$il=$method.GetMethodBody().GetILAsByteArray(); $sha=[Security.Cryptography.SHA256]::Create()
	try { $digest=[BitConverter]::ToString($sha.ComputeHash($il)).Replace('-','').ToLowerInvariant() } finally { $sha.Dispose() }
	[pscustomobject]@{ Token=[int]$method.MetadataToken; Mvid=$method.Module.ModuleVersionId.ToString('D'); Signature=$Signature; IlSha256=$digest }
}

try {
	Remove-Item -LiteralPath $statusPath,$observedPath,$instanceObservedPath,$finalizerObservedPath,$stopPath -Force -ErrorAction SilentlyContinue
	$fixtureEscaped=$fixtureDll.Replace("'","''"); $observedEscaped=$observedPath.Replace("'","''"); $instanceObservedEscaped=$instanceObservedPath.Replace("'","''"); $finalizerObservedEscaped=$finalizerObservedPath.Replace("'","''"); $stopEscaped=$stopPath.Replace("'","''")
	$childBody = "Add-Type -Path '$fixtureEscaped'`n`$instance=[HookLabPowerShellFixture.InstanceTarget]::new(5)`nwhile(-not (Test-Path -LiteralPath '$stopEscaped')) { [IO.File]::WriteAllText('$observedEscaped',[HookLabPowerShellFixture.Target]::Calculate(41).ToString()); [IO.File]::WriteAllText('$instanceObservedEscaped',`$instance.Calculate(1).ToString()); try { `$finalizerValue=[HookLabPowerShellFixture.Target]::ThrowOrReturn(`$true).ToString() } catch { `$finalizerValue='THREW:'+`$_.Exception.GetBaseException().GetType().Name }; [IO.File]::WriteAllText('$finalizerObservedEscaped',`$finalizerValue); Start-Sleep -Milliseconds 100 }"
	[IO.File]::WriteAllText($childScript,$childBody,[Text.UTF8Encoding]::new($false))
	$child=Start-Process -FilePath "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-File',('"'+$childScript+'"') -PassThru -WindowStyle Hidden
	Status "CHILD_STARTED pid=$($child.Id)"
	if(-not (Wait-Observed 42)) { throw 'baseline did not become 42' }
	Status 'BASELINE_OK value=42'
	if(-not (Wait-Observed 6 $instanceObservedPath)) { throw 'instance baseline did not become 6' }
	Status 'INSTANCE_BASELINE_OK value=6'
	if(-not (Wait-Text 'THREW:InvalidOperationException' $finalizerObservedPath)) { throw 'finalizer baseline did not throw InvalidOperationException' }
	Status 'FINALIZER_BASELINE_OK exception=InvalidOperationException'

	$hostProcessId=& (Join-Path $PSScriptRoot 'TestSupport\Start-DgSpyHost.ps1') -TargetFramework net10.0-windows
	$rpcPort=[int]$env:DGSPY_RPC_PORT
	Status "HOST_STARTED pid=$hostProcessId rpc_port=$rpcPort"
	$program=@(Rpc 'list_programs' @{process_ids=@($child.Id)})[0]
	$sessionId=(Rpc 'attach' @{program_id=$program.program_id} 60).session_id
	$facts=Facts $fixtureDll 'HookLabPowerShellFixture.Target' 'System.Int32 Calculate(System.Int32)'
	$moduleResult=Rpc 'list_modules' @{session_id=$sessionId;count=500}
	[IO.File]::WriteAllText((Join-Path $RunDirectory 'modules.json'),(ConvertTo-Json -InputObject $moduleResult -Depth 8),[Text.UTF8Encoding]::new($false))
	Status "MODULE_RESPONSE type=$($moduleResult.GetType().FullName) count=$(@($moduleResult.modules).Count)"
	$module=@($moduleResult.modules | Where-Object { $_.mvid -eq $facts.Mvid -or $_.filename -like '*HookLabPowerShellFixture*' -or $_.name -like '*HookLabPowerShellFixture*' } | Select-Object -First 1)[0]
	if(-not $module) { throw 'fixture module was not found' }
	Status "ATTACHED session=$sessionId module_id=$($module.module_id)"
	$beforeTemplate=Rpc 'get_hooklab_status' @{session_id=$sessionId;process_id=$child.Id}
	if($beforeTemplate.initialized) { throw 'HookLab was initialized before the template request' }
	$template=Rpc 'get_hook_template' @{session_id=$sessionId;module_id=$module.module_id;method_token=$facts.Token;template='PrefixPostfix'}
	if($template.source -notlike '*public static void Prefix*' -or $template.source -notlike '*public static void Postfix*' -or $template.source -notlike '*out object __state*' -or $template.source -like '*__instance*') { throw 'static PrefixPostfix template shape was incorrect' }
	$afterTemplate=Rpc 'get_hooklab_status' @{session_id=$sessionId;process_id=$child.Id}
	if($afterTemplate.initialized) { throw 'read-only template request initialized HookLab' }
	$instanceFacts=Facts $fixtureDll 'HookLabPowerShellFixture.InstanceTarget' 'System.Int32 Calculate(System.Int32)'
	$instanceTemplate=Rpc 'get_hook_template' @{session_id=$sessionId;module_id=$module.module_id;method_token=$instanceFacts.Token;template='Postfix'}
	if($instanceTemplate.source -notlike '*HookLabPowerShellFixture.InstanceTarget __instance*' -or $instanceTemplate.source -notlike '*ref System.Int32 __result*') { throw 'instance Postfix template shape was incorrect' }
	$finalizerFacts=Facts $fixtureDll 'HookLabPowerShellFixture.Target' 'System.Int32 ThrowOrReturn(System.Boolean)' 'ThrowOrReturn'
	$finalizerTemplate=Rpc 'get_hook_template' @{session_id=$sessionId;module_id=$module.module_id;method_token=$finalizerFacts.Token;template='Finalizer'}
	if($finalizerTemplate.source -notlike '*System.Exception Finalizer*' -or $finalizerTemplate.source -notlike '*System.Exception __exception*' -or $finalizerTemplate.source -notlike '*return __exception*') { throw 'Finalizer template shape was incorrect' }
	try { $null=Rpc 'get_hook_template' @{session_id=$sessionId;module_id=$module.module_id;method_token=$facts.Token;template='Transpiler'}; throw 'invalid template unexpectedly succeeded' } catch { if($_.Exception.Message -notlike '*invalid_arguments*') { throw } }
	$fixtureAssembly=[Reflection.Assembly]::LoadFrom($fixtureDll)
	$fieldToken=[int]$fixtureAssembly.GetType('HookLabPowerShellFixture.InstanceTarget',$true).GetField('Offset').MetadataToken
	try { $null=Rpc 'get_hook_template' @{session_id=$sessionId;module_id=$module.module_id;method_token=$fieldToken}; throw 'field token unexpectedly produced a method template' } catch { if($_.Exception.Message -notlike '*method_not_found*') { throw } }
	$genericMethod=$fixtureAssembly.GetType('HookLabPowerShellFixture.Target',$true).GetMethod('Identity')
	if(-not $genericMethod) { throw 'fixture is stale: generic Identity method is missing; rebuild HookLabPowerShellFixture' }
	$genericToken=[int]$genericMethod.MetadataToken
	try { $null=Rpc 'get_hook_template' @{session_id=$sessionId;module_id=$module.module_id;method_token=$genericToken}; throw 'generic method unexpectedly produced a template' } catch { if($_.Exception.Message -notlike '*unsupported_hook_target*') { throw } }
	Status 'HOOK_TEMPLATE_READ_ONLY_OK static=PrefixPostfix instance=Postfix finalizer=preserving invalid=refused non_method=refused generic=refused initialized=false'
	$base=@{session_id=$sessionId;process_id=$child.Id;hook_id='powershell-calculate';kind='Prefix';module_id=$module.module_id;assembly='HookLabPowerShellFixture';declaring_type='HookLabPowerShellFixture.Target';method='Calculate';method_token=$facts.Token;signature=$facts.Signature;module_mvid=$facts.Mvid;il_sha256=$facts.IlSha256}
	$base.source='public static class PowerShellPairV1 { public static void Prefix(ref int value, out int __state) { __state = value; value += 10; } public static void Postfix(int __state, ref int __result) { __result += __state; } }'; $base.revision=1
	$created=Rpc 'create_hook' $base 70
	if($created.hook.revision -ne 1 -or $created.hook.source -ne $base.source -or $created.hook.diagnostics.Count -ne 0) { throw 'create_hook did not publish editable source and successful diagnostics state' }
	try { $null=Rpc 'create_hook' $base 70; throw 'duplicate create unexpectedly succeeded' } catch { if($_.Exception.Message -notlike '*hook_exists*') { throw } }
	$missing=@{}; foreach($pair in $base.GetEnumerator()) { $missing[$pair.Key]=$pair.Value }; $missing.hook_id='missing-compiled-hook'; $missing.revision=2
	try { $null=Rpc 'update_hook' $missing 70; throw 'missing update unexpectedly succeeded' } catch { if($_.Exception.Message -notlike '*hook_not_found*') { throw } }
	Status 'CREATE_UPDATE_LIFECYCLE_OK duplicate=refused missing=refused source=listed diagnostics=empty'
	if(-not (Wait-Observed 93)) { throw 'paired revision 1 did not mutate 41 to 51, return 52, and add shared state 41' }
	Status 'PREFIX_POSTFIX_STATE_OK value=93'

	$base.kind='Postfix'; $base.source='public static class PowerShellPostfixV2 { public static void Postfix(int value, ref int __result) { __result += value; } }'; $base.revision=2
	$null=Rpc 'update_hook' $base 70
	if(-not (Wait-Observed 83)) { throw 'Postfix revision 2 did not preserve the original 42 and add named argument value 41' }
	Status 'POSTFIX_NAMED_ARGUMENT_OK value=83'

	$base.source='this is not C#'; $base.revision=3
	try { $null=Rpc 'update_hook' $base 70; throw 'broken revision unexpectedly installed' } catch { if($_.Exception.Message -notlike '*CS*') { throw }; Status 'BROKEN_UPDATE_REJECTED diagnostics=CS' }
	if(-not (Wait-Observed 83)) { throw 'broken update did not preserve Postfix result 83' }
	Status 'ROLLBACK_OK value=83'

	$base.source='public static class PowerShellPostfixV3 { public static void Postfix(ref int __result) { __result += 200; } }'
	$null=Rpc 'update_hook' $base 70
	if(-not (Wait-Observed 242)) { throw 'Postfix revision 3 did not produce 242' }
	Status 'POSTFIX_REVISION_3_OK value=242'
	$disabled=Rpc 'disable_hook' @{session_id=$sessionId;process_id=$child.Id;hook_id='powershell-calculate'} 30
	if($disabled.changed -ne $true -or $disabled.hook.enabled -ne $false -or $disabled.hook.revision -ne 3 -or -not (Wait-Observed 42)) { throw 'disable did not preserve revision 3 while restoring original behavior 42' }
	$idempotent=Rpc 'disable_hook' @{session_id=$sessionId;process_id=$child.Id;hook_id='powershell-calculate'} 30
	if($idempotent.changed -ne $false) { throw 'repeated disable was not idempotent' }
	Status 'DISABLE_PRESERVES_COMPILED_STATE_OK revision=3 value=42'
	$enabled=Rpc 'enable_hook' @{session_id=$sessionId;process_id=$child.Id;hook_id='powershell-calculate'} 30
	if($enabled.changed -ne $true -or $enabled.hook.enabled -ne $true -or $enabled.hook.revision -ne 3 -or -not (Wait-Observed 242)) { throw 'enable did not reactivate revision 3 without recompilation' }
	Status 'ENABLE_RESTORES_COMPILED_STATE_OK revision=3 value=242'
	$observer=@{}; foreach($pair in $base.GetEnumerator()) { $observer[$pair.Key]=$pair.Value }
	$observer.Remove('source'); $observer.Remove('revision'); $observer.hook_id='powershell-calculate-observer'; $observer.kind='Postfix'
	$null=Rpc 'install_hook' $observer 30
	if(-not (Wait-Observed 242)) { throw 'observational Postfix suppressed the compiled Postfix result' }
	Status 'OBSERVER_COMPILED_COEXIST_OK value=242'
	$null=Rpc 'remove_hook' @{session_id=$sessionId;process_id=$child.Id;hook_id='powershell-calculate-observer'} 30
	if(-not (Wait-Observed 242)) { throw 'removing the observational Postfix removed the compiled Postfix' }
	Status 'OBSERVER_REMOVE_PRESERVES_COMPILED_OK value=242'
	$null=Rpc 'remove_hook' @{session_id=$sessionId;process_id=$child.Id;hook_id='powershell-calculate'} 30
	if(-not (Wait-Observed 42)) { throw 'removal did not restore 42' }
	Status 'REMOVE_OK value=42'
	$finalizer=@{session_id=$sessionId;process_id=$child.Id;hook_id='powershell-finalizer';kind='Finalizer';module_id=$module.module_id;assembly='HookLabPowerShellFixture';declaring_type='HookLabPowerShellFixture.Target';method='ThrowOrReturn';method_token=$finalizerFacts.Token;signature=$finalizerFacts.Signature;module_mvid=$finalizerFacts.Mvid;il_sha256=$finalizerFacts.IlSha256;revision=1;source='public static class PowerShellFinalizerV1 { public static System.Exception Finalizer(System.Exception __exception) { return null; } }'}
	$null=Rpc 'create_hook' $finalizer 70
	if(-not(Wait-Text '0' $finalizerObservedPath)){throw 'compiled Finalizer did not suppress the exception and expose the default int result'}
	Status 'FINALIZER_SUPPRESS_OK value=0'
	$finalizer.source='this is not C#';$finalizer.revision=2;try{$null=Rpc 'update_hook' $finalizer 70;throw'broken Finalizer unexpectedly installed'}catch{if($_.Exception.Message-notlike'*CS*'){throw}}
	if(-not(Wait-Text '0' $finalizerObservedPath)){throw 'failed Finalizer update did not retain suppression'}
	Status 'FINALIZER_ROLLBACK_OK value=0'
	$null=Rpc 'disable_hook' @{session_id=$sessionId;process_id=$child.Id;hook_id='powershell-finalizer'} 30;if(-not(Wait-Text 'THREW:InvalidOperationException' $finalizerObservedPath)){throw 'disabled Finalizer did not restore original exception'}
	$null=Rpc 'enable_hook' @{session_id=$sessionId;process_id=$child.Id;hook_id='powershell-finalizer'} 30;if(-not(Wait-Text '0' $finalizerObservedPath)){throw 're-enabled Finalizer did not restore suppression'}
	Status 'FINALIZER_TOGGLE_OK disabled=throws enabled=value0'
	$null=Rpc 'remove_hook' @{session_id=$sessionId;process_id=$child.Id;hook_id='powershell-finalizer'} 30;if(-not(Wait-Text 'THREW:InvalidOperationException' $finalizerObservedPath)){throw 'Finalizer removal did not restore original exception'}
	Status 'FINALIZER_REMOVE_OK exception=InvalidOperationException'

	$instanceRequest=@{session_id=$sessionId;process_id=$child.Id;hook_id='powershell-instance-calculate';kind='Postfix';module_id=$module.module_id;assembly='HookLabPowerShellFixture';declaring_type='HookLabPowerShellFixture.InstanceTarget';method='Calculate';method_token=$instanceFacts.Token;signature=$instanceFacts.Signature;module_mvid=$instanceFacts.Mvid;il_sha256=$instanceFacts.IlSha256;revision=1;source='public static class PowerShellInstancePostfix { public static void Postfix(HookLabPowerShellFixture.InstanceTarget __instance, ref int __result) { __result += __instance.Offset; } }'}
	$null=Rpc 'install_hook' $instanceRequest 70
	if(-not (Wait-Observed 11 $instanceObservedPath)) { throw 'instance Postfix did not receive Offset 5 and produce 11' }
	Status 'INSTANCE_POSTFIX_OK value=11'
	$null=Rpc 'remove_hook' @{session_id=$sessionId;process_id=$child.Id;hook_id='powershell-instance-calculate'} 30
	if(-not (Wait-Observed 6 $instanceObservedPath)) { throw 'instance removal did not restore 6' }
	Status 'INSTANCE_REMOVE_OK value=6'
	Status 'RESULT pass'
}
catch {
	Status ('ERROR ' + $_.Exception.Message.Replace("`r",' ').Replace("`n",' '))
	throw
}
finally {
	if($sessionId) { try { $state=Rpc 'get_session_state' @{session_id=$sessionId}; if($state.state -eq 'paused') { $null=Rpc 'continue' @{session_id=$sessionId} }; $null=Rpc 'detach' @{session_id=$sessionId} 30 } catch {} }
	[IO.File]::WriteAllText($stopPath,'stop')
	if($child) { if(-not $child.WaitForExit(5000)) { Stop-Process -Id $child.Id -Force -ErrorAction SilentlyContinue } }
	if($hostProcessId) { Stop-Process -Id $hostProcessId -Force -ErrorAction SilentlyContinue }
	Status 'CLEANUP_DONE'
}
