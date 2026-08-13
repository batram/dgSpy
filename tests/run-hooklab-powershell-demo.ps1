#requires -Version 5.1
<#
.SYNOPSIS
    Visible, interactive HookLab custom Prefix and Postfix playground.

.DESCRIPTION
    Launch this script in a visible Windows PowerShell console. It opens the packaged dnSpy GUI,
    attaches to a disposable Windows PowerShell 5.1 target, and leaves hook changes under menu control.
    Q detaches and removes only the processes created by this demo.
#>
$ErrorActionPreference = 'Stop'
$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new()
$repoRoot = Split-Path $PSScriptRoot -Parent
$runRoot = Join-Path $PSScriptRoot 'artifacts\hooklab-powershell-demo'
$fixtureDll = Join-Path $repoRoot 'tests\TestTargets\HookLabPowerShellFixture\bin\Release\net48\HookLabPowerShellFixture.dll'
$observedPath = Join-Path $runRoot 'observed.txt'
$stopPath = Join-Path $runRoot 'stop'
$childScript = Join-Path $runRoot 'child.ps1'
$hostProcessId = 0
$child = $null
$sessionId = $null
$rpcPort = 0
$activeRevision = 0
$activeKind = $null
$hookInstalled = $false
. (Join-Path $PSScriptRoot 'TestSupport\Invoke-DgSpyRpc.ps1')

function Rpc([string]$Operation,[hashtable]$Arguments=@{},[int]$Deadline=30) {
	Invoke-DgSpyRpc -OperationName $Operation -OperationArguments $Arguments -RpcPort $rpcPort -DeadlineSeconds $Deadline
}
function Wait-Value([int]$Expected,[int]$Seconds=10) {
	$deadline=[DateTime]::UtcNow.AddSeconds($Seconds)
	do { if((Read-Value) -eq $Expected) { return $true }; Start-Sleep -Milliseconds 100 } while([DateTime]::UtcNow -lt $deadline)
	return $false
}
function Read-Value {
	if(Test-Path -LiteralPath $observedPath) { return [int](Get-Content -LiteralPath $observedPath -Raw) }
	return $null
}
function Facts([string]$Path) {
	$assembly=[Reflection.Assembly]::LoadFrom($Path)
	$method=$assembly.GetType('HookLabPowerShellFixture.Target',$true).GetMethod('Calculate')
	$il=$method.GetMethodBody().GetILAsByteArray(); $sha=[Security.Cryptography.SHA256]::Create()
	try { $digest=[BitConverter]::ToString($sha.ComputeHash($il)).Replace('-','').ToLowerInvariant() } finally { $sha.Dispose() }
	[pscustomobject]@{Token=[int]$method.MetadataToken;Mvid=$method.Module.ModuleVersionId.ToString('D');Signature='System.Int32 Calculate(System.Int32)';IlSha256=$digest}
}
function Show-Menu {
	Write-Host ''
	Write-Host 'HookLab Windows PowerShell playground' -ForegroundColor Cyan
	Write-Host ('Current Calculate(41) result: ' + (Read-Value)) -ForegroundColor Yellow
	Write-Host ('Active compiled hook: ' + $(if($hookInstalled){"$activeKind revision $activeRevision"}else{'none'}))
	Write-Host '  1  Prefix: skip original and return 777'
	Write-Host '  2  Postfix: let original return 42, then add 100'
	Write-Host '  3  Try a deliberately broken next revision'
	Write-Host '  4  Remove hook and restore original result 42'
	Write-Host '  5  Show current hook record'
	Write-Host '  Q  Detach and clean up'
}
function Apply-Hook([string]$Kind,[string]$Source,[int]$Expected) {
	$next=$activeRevision+1
	$request=$script:hookRequest.Clone()
	$request.kind=$Kind; $request.revision=$next; $request.source=$Source
	$response=Rpc 'install_hook' $request 70
	if(-not (Wait-Value $Expected)) { throw "$Kind revision $next installed but the observed result did not become $Expected" }
	$script:activeRevision=$next; $script:activeKind=$Kind; $script:hookInstalled=$true
	Write-Host "Applied $Kind revision $next. Live result is now $Expected." -ForegroundColor Green
	$response.hook | Format-List | Out-Host
}
function Try-Broken {
	$next=$activeRevision+1
	$request=$script:hookRequest.Clone(); $request.revision=$next; $request.source='this is not C#'
	try { $null=Rpc 'install_hook' $request 70; Write-Host 'Unexpectedly accepted broken source.' -ForegroundColor Red }
	catch {
		Write-Host 'Compiler rejected the candidate, as intended:' -ForegroundColor Green
		Write-Host $_.Exception.Message -ForegroundColor DarkYellow
		Write-Host ('Active result remains ' + (Read-Value) + '; revision was not advanced.') -ForegroundColor Yellow
	}
}

try {
	$Host.UI.RawUI.WindowTitle='HookLab visible PowerShell playground'
	Clear-Host
	Write-Host 'HOOKLAB DEMO IS SETTING UP' -ForegroundColor Cyan
	Write-Host 'Please wait. Do not choose a menu option until READY appears.' -ForegroundColor Yellow
	Write-Host ''
	New-Item -ItemType Directory -Force -Path $runRoot | Out-Null
	Remove-Item -LiteralPath $observedPath,$stopPath -Force -ErrorAction SilentlyContinue
	if(-not (Test-Path -LiteralPath $fixtureDll)) { throw "Fixture DLL is missing: $fixtureDll" }
	$fixtureEscaped=$fixtureDll.Replace("'","''"); $observedEscaped=$observedPath.Replace("'","''"); $stopEscaped=$stopPath.Replace("'","''")
	$body="Add-Type -Path '$fixtureEscaped'`nwhile(-not (Test-Path -LiteralPath '$stopEscaped')) { [IO.File]::WriteAllText('$observedEscaped',[HookLabPowerShellFixture.Target]::Calculate(41).ToString()); Start-Sleep -Milliseconds 100 }"
	[IO.File]::WriteAllText($childScript,$body,[Text.UTF8Encoding]::new($false))
	$child=Start-Process "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-File',('"'+$childScript+'"') -PassThru -WindowStyle Hidden
	if(-not (Wait-Value 42)) { throw 'Disposable PowerShell target did not publish baseline 42.' }
	Write-Host "Disposable Windows PowerShell target PID $($child.Id), baseline result 42." -ForegroundColor Green

	$env:DGSPY_LAYOUT_ROOT=Join-Path $repoRoot 'artifacts\layouts\local'
	$hostProcessId=& (Join-Path $PSScriptRoot 'TestSupport\Start-DgSpyHost.ps1') -TargetFramework net10.0-windows
	$rpcPort=[int]$env:DGSPY_RPC_PORT
	$program=@(Rpc 'list_programs' @{process_ids=@($child.Id)})[0]
	$sessionId=(Rpc 'attach' @{program_id=$program.program_id} 60).session_id
	$facts=Facts $fixtureDll
	$modules=Rpc 'list_modules' @{session_id=$sessionId;count=500}
	$module=@($modules.modules | Where-Object { $_.mvid -eq $facts.Mvid -or $_.filename -like '*HookLabPowerShellFixture*' -or $_.name -like '*HookLabPowerShellFixture*' } | Select-Object -First 1)[0]
	if(-not $module) { throw 'HookLabPowerShellFixture was not visible in the attached target.' }
	$script:hookRequest=@{session_id=$sessionId;process_id=$child.Id;hook_id='powershell-calculate';kind='Prefix';module_id=$module.module_id;assembly='HookLabPowerShellFixture';declaring_type='HookLabPowerShellFixture.Target';method='Calculate';method_token=$facts.Token;signature=$facts.Signature;module_mvid=$facts.Mvid;il_sha256=$facts.IlSha256}
	Write-Host "dnSpy attached. Exact module: $($module.module_id)" -ForegroundColor Green
	Write-Host ''
	Write-Host '===================== READY =====================' -ForegroundColor Green
	Write-Host 'The playground is ready. Nothing is patched until you choose 1 or 2.' -ForegroundColor Cyan

	$done=$false
	while(-not $done) {
		Show-Menu
		switch((Read-Host 'Choose').Trim().ToUpperInvariant()) {
			'1' { $next=$activeRevision+1; Apply-Hook 'Prefix' "public static class PowerShellPrefixR$next { public static bool Prefix(ref int __result) { __result=777; return false; } }" 777 }
			'2' { $next=$activeRevision+1; Apply-Hook 'Postfix' "public static class PowerShellPostfixR$next { public static void Postfix(ref int __result) { __result += 100; } }" 142 }
			'3' { Try-Broken }
			'4' {
				if($hookInstalled) { $null=Rpc 'remove_hook' @{session_id=$sessionId;process_id=$child.Id;hook_id='powershell-calculate'} 30; $hookInstalled=$false; $activeRevision=0 }
				if(-not (Wait-Value 42)) { throw 'Original behavior was not restored.' }
				Write-Host 'Hook removed. Live result is 42 again.' -ForegroundColor Green
			}
			'5' { $listed=Rpc 'list_hooks' @{session_id=$sessionId;process_id=$child.Id}; Write-Host ('Live result: '+(Read-Value)) -ForegroundColor Yellow; $listed.hooks | Format-List | Out-Host }
			'Q' { $done=$true }
			default { Write-Host 'Choose 1, 2, 3, 4, 5, or Q.' -ForegroundColor DarkYellow }
		}
	}
}
catch {
	Write-Host ''
	Write-Host '==================== SETUP FAILED ====================' -ForegroundColor Red
	Write-Host $_.Exception.Message -ForegroundColor Red
	Write-Host 'The numbered playground menu was NOT started.' -ForegroundColor Yellow
	Write-Host 'This window closes only when you enter Q.' -ForegroundColor Yellow
	do { $failureChoice=(Read-Host 'Enter Q to clean up and close').Trim().ToUpperInvariant() } while($failureChoice -ne 'Q')
}
finally {
	if($sessionId) { try { if($hookInstalled){$null=Rpc 'remove_hook' @{session_id=$sessionId;process_id=$child.Id;hook_id='powershell-calculate'} 30}; $state=Rpc 'get_session_state' @{session_id=$sessionId}; if($state.state -eq 'paused'){$null=Rpc 'continue' @{session_id=$sessionId}}; $null=Rpc 'detach' @{session_id=$sessionId} 30 } catch{} }
	[IO.File]::WriteAllText($stopPath,'stop')
	if($child -and -not $child.WaitForExit(5000)){Stop-Process -Id $child.Id -Force -ErrorAction SilentlyContinue}
	if($hostProcessId){Stop-Process -Id $hostProcessId -Force -ErrorAction SilentlyContinue}
	Write-Host 'Demo cleanup complete. You may close this window.' -ForegroundColor Cyan
}
