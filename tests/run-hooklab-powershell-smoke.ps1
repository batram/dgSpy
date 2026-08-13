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
function Wait-Observed([int]$Expected,[int]$Seconds=10) {
	$deadline=[DateTime]::UtcNow.AddSeconds($Seconds)
	do { if(Test-Path -LiteralPath $observedPath) { $value=[int](Get-Content -LiteralPath $observedPath -Raw); if($value -eq $Expected) { return $true } }; Start-Sleep -Milliseconds 100 } while([DateTime]::UtcNow -lt $deadline)
	return $false
}
function Facts([string]$Path) {
	$assembly=[Reflection.Assembly]::LoadFrom($Path); $method=$assembly.GetType('HookLabPowerShellFixture.Target',$true).GetMethod('Calculate')
	$il=$method.GetMethodBody().GetILAsByteArray(); $sha=[Security.Cryptography.SHA256]::Create()
	try { $digest=[BitConverter]::ToString($sha.ComputeHash($il)).Replace('-','').ToLowerInvariant() } finally { $sha.Dispose() }
	[pscustomobject]@{ Token=[int]$method.MetadataToken; Mvid=$method.Module.ModuleVersionId.ToString('D'); Signature='System.Int32 Calculate(System.Int32)'; IlSha256=$digest }
}

try {
	Remove-Item -LiteralPath $statusPath,$observedPath,$stopPath -Force -ErrorAction SilentlyContinue
	$fixtureEscaped=$fixtureDll.Replace("'","''"); $observedEscaped=$observedPath.Replace("'","''"); $stopEscaped=$stopPath.Replace("'","''")
	$childBody = "Add-Type -Path '$fixtureEscaped'`nwhile(-not (Test-Path -LiteralPath '$stopEscaped')) { [IO.File]::WriteAllText('$observedEscaped',[HookLabPowerShellFixture.Target]::Calculate(41).ToString()); Start-Sleep -Milliseconds 100 }"
	[IO.File]::WriteAllText($childScript,$childBody,[Text.UTF8Encoding]::new($false))
	$child=Start-Process -FilePath "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-File',('"'+$childScript+'"') -PassThru -WindowStyle Hidden
	Status "CHILD_STARTED pid=$($child.Id)"
	if(-not (Wait-Observed 42)) { throw 'baseline did not become 42' }
	Status 'BASELINE_OK value=42'

	$hostProcessId=& (Join-Path $PSScriptRoot 'TestSupport\Start-DgSpyHost.ps1') -TargetFramework net10.0-windows
	$rpcPort=[int]$env:DGSPY_RPC_PORT
	Status "HOST_STARTED pid=$hostProcessId rpc_port=$rpcPort"
	$program=@(Rpc 'list_programs' @{process_ids=@($child.Id)})[0]
	$sessionId=(Rpc 'attach' @{program_id=$program.program_id} 60).session_id
	$facts=Facts $fixtureDll
	$moduleResult=Rpc 'list_modules' @{session_id=$sessionId;count=500}
	[IO.File]::WriteAllText((Join-Path $RunDirectory 'modules.json'),(ConvertTo-Json -InputObject $moduleResult -Depth 8),[Text.UTF8Encoding]::new($false))
	Status "MODULE_RESPONSE type=$($moduleResult.GetType().FullName) count=$(@($moduleResult.modules).Count)"
	$module=@($moduleResult.modules | Where-Object { $_.mvid -eq $facts.Mvid -or $_.filename -like '*HookLabPowerShellFixture*' -or $_.name -like '*HookLabPowerShellFixture*' } | Select-Object -First 1)[0]
	if(-not $module) { throw 'fixture module was not found' }
	Status "ATTACHED session=$sessionId module_id=$($module.module_id)"
	$base=@{session_id=$sessionId;process_id=$child.Id;hook_id='powershell-calculate';kind='Prefix';module_id=$module.module_id;assembly='HookLabPowerShellFixture';declaring_type='HookLabPowerShellFixture.Target';method='Calculate';method_token=$facts.Token;signature=$facts.Signature;module_mvid=$facts.Mvid;il_sha256=$facts.IlSha256}
	$base.source='public static class PowerShellPrefixV1 { public static void Prefix(ref int value) { value += 10; } }'; $base.revision=1
	$null=Rpc 'install_hook' $base 70
	if(-not (Wait-Observed 52)) { throw 'Prefix revision 1 did not mutate value from 41 to 51 before the original returned 52' }
	Status 'PREFIX_NAMED_ARGUMENT_OK value=52'

	$base.kind='Postfix'; $base.source='public static class PowerShellPostfixV2 { public static void Postfix(int value, ref int __result) { __result += value; } }'; $base.revision=2
	$null=Rpc 'install_hook' $base 70
	if(-not (Wait-Observed 83)) { throw 'Postfix revision 2 did not preserve the original 42 and add named argument value 41' }
	Status 'POSTFIX_NAMED_ARGUMENT_OK value=83'

	$base.source='this is not C#'; $base.revision=3
	try { $null=Rpc 'install_hook' $base 70; throw 'broken revision unexpectedly installed' } catch { if($_.Exception.Message -notlike '*CS*') { throw }; Status 'BROKEN_UPDATE_REJECTED diagnostics=CS' }
	if(-not (Wait-Observed 83)) { throw 'broken update did not preserve Postfix result 83' }
	Status 'ROLLBACK_OK value=83'

	$base.source='public static class PowerShellPostfixV3 { public static void Postfix(ref int __result) { __result += 200; } }'
	$null=Rpc 'install_hook' $base 70
	if(-not (Wait-Observed 242)) { throw 'Postfix revision 3 did not produce 242' }
	Status 'POSTFIX_REVISION_3_OK value=242'
	$null=Rpc 'remove_hook' @{session_id=$sessionId;process_id=$child.Id;hook_id='powershell-calculate'} 30
	if(-not (Wait-Observed 42)) { throw 'removal did not restore 42' }
	Status 'REMOVE_OK value=42'
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
