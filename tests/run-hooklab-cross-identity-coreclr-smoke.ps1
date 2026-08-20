#requires -Version 5.1
<#
Cross-identity CoreCLR HookLab lifecycle smoke. ASCII-only; run on a hidden desktop; must run elevated.

The CoreCLR half of the target environment contract, subslice 8. Its net48 sibling
(run-hooklab-cross-identity-smoke.ps1) covers two axes; this one covers exactly one, and the
difference is a property of the runtime rather than a gap:

  .NET Core has a single application domain. The multi-domain arrangement that hid defect 5 of the
  2026-08-19 incident cannot exist on CoreCLR at all, so there is no app_domain_id anywhere below and
  no domain assertion to make. What remains - and what no CoreCLR gate covered before this - is the
  identity axis: a debugger and a target owned by different accounts.

Needs the same dgspy-fixture account as its sibling; see that script's header for the one-liner. It
refuses to fall back to a same-user run, and it must be elevated because attaching across accounts
requires SeDebugPrivilege.
#>
[CmdletBinding()]
param(
	[int]$RpcPort = 0,
	[string]$RunDirectory,
	[string]$FixtureUser = 'dgspy-fixture',
	[string]$FixturePassword = 'DgSpyFixture!2026'
)
$ErrorActionPreference = 'Stop'
$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new()
$repoRoot = Split-Path $PSScriptRoot -Parent
$targetDir = Join-Path $repoRoot 'tests\TestTargets\HookLabCrossIdentityCoreTarget\bin\Release\net10.0'
$root = if ($RunDirectory) { $RunDirectory } else { Join-Path $PSScriptRoot ('artifacts\cross-identity-coreclr-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
$hostId = 0; $target = $null; $sessionId = $null; $pass = 0; $fail = 0; $stage = $null
. (Join-Path $PSScriptRoot 'TestSupport\Invoke-DgSpyRpc.ps1')
. (Join-Path $PSScriptRoot 'TestSupport\Find-DgSpyProgram.ps1')
function Rpc([string]$Operation, [hashtable]$Arguments = @{}, [int]$Deadline = 70) { Invoke-DgSpyRpc -OperationName $Operation -OperationArguments $Arguments -RpcPort $RpcPort -DeadlineSeconds $Deadline }
function Check([string]$Name, $Condition, [string]$Detail = '') { if ([bool]$Condition) { $script:pass++; Write-Host "PASS  $Name" -ForegroundColor DarkGreen } else { $script:fail++; Write-Host "FAIL  $Name  $Detail" -ForegroundColor Red } }
function Lines { @(Get-Content -LiteralPath $script:factsFile -ErrorAction SilentlyContinue) }
function Fact([string]$Name) { ((Lines | Where-Object { $_ -like "$Name *" } | Select-Object -First 1) -replace ('^' + $Name + ' '), '') }
function Wait-Marker([string]$Text, [int]$Seconds = 30) { $deadline = [DateTime]::UtcNow.AddSeconds($Seconds); do { Start-Sleep -Milliseconds 200; if (@(Lines | Where-Object { $_ -eq $Text }).Count) { return $true } } until ([DateTime]::UtcNow -gt $deadline); return $false }
function Wait-Tick([int]$Seconds = 15) { $before = @(Lines | Where-Object { $_ -like 'TICK *' }).Count; $deadline = [DateTime]::UtcNow.AddSeconds($Seconds); do { Start-Sleep -Milliseconds 200; if (@(Lines | Where-Object { $_ -like 'TICK *' }).Count -gt $before) { return $true } } until ([DateTime]::UtcNow -gt $deadline); return $false }

try {
	New-Item -ItemType Directory -Force -Path $root | Out-Null
	Start-Transcript -LiteralPath (Join-Path $root 'driver.log') -Force | Out-Null

	if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
		throw "This smoke must run elevated. It debugs a process owned by '$FixtureUser', and attaching across accounts requires SeDebugPrivilege, which a standard token does not have - dnSpy cannot even list the target without it."
	}
	if (-not (Get-LocalUser -Name $FixtureUser -ErrorAction SilentlyContinue)) {
		throw "The fixture account '$FixtureUser' does not exist. Create it elevated; see the header of tests\run-hooklab-cross-identity-smoke.ps1. This smoke refuses to fall back to a same-user run."
	}
	Add-Type -AssemblyName System.DirectoryServices.AccountManagement
	$context = New-Object System.DirectoryServices.AccountManagement.PrincipalContext([System.DirectoryServices.AccountManagement.ContextType]::Machine)
	if (-not $context.ValidateCredentials($FixtureUser, $FixturePassword)) {
		throw "The fixture account '$FixtureUser' exists but its password does not match. Reset it elevated: Set-LocalUser -Name $FixtureUser -Password (ConvertTo-SecureString '$FixturePassword' -AsPlainText -Force)"
	}
	if (-not (Test-Path (Join-Path $targetDir 'HookLabCrossIdentityCoreTarget.exe'))) {
		throw "Build the fixture first: dotnet build tests\TestTargets\HookLabCrossIdentityCoreTarget\HookLabCrossIdentityCoreTarget.csproj -c Release"
	}
	if (-not $env:DGSPY_LAYOUT_ROOT) {
		throw "DGSPY_LAYOUT_ROOT is not set. Point it at a completed layout, for example: `$env:DGSPY_LAYOUT_ROOT = '$repoRoot\artifacts\layouts\<build-id>'. The modernization gate sets this for you; a standalone run does not."
	}

	$stage = Join-Path $env:ProgramData ('dgSpy\fixture\' + [Guid]::NewGuid().ToString('N'))
	New-Item -ItemType Directory -Force -Path $stage | Out-Null
	Copy-Item (Join-Path $targetDir '*') -Destination $stage -Recurse -Force
	$script:factsFile = Join-Path $stage 'facts.txt'
	New-Item -ItemType File -Path $script:factsFile -Force | Out-Null
	& icacls $stage /grant ("$FixtureUser" + ':(OI)(CI)(RX)') | Out-Null
	& icacls $script:factsFile /grant ("$FixtureUser" + ':(M)') | Out-Null

	$credential = New-Object System.Management.Automation.PSCredential($FixtureUser, (ConvertTo-SecureString $FixturePassword -AsPlainText -Force))
	Start-Process -FilePath (Join-Path $stage 'HookLabCrossIdentityCoreTarget.exe') -ArgumentList $script:factsFile -Credential $credential -WorkingDirectory $stage -WindowStyle Hidden
	if (-not (Wait-Marker 'READY' 60)) { throw ('The CoreCLR cross-identity target did not become ready. Facts: ' + ((Lines) -join ' | ')) }
	$targetPid = [int](Fact 'PROCESS')
	$target = Get-Process -Id $targetPid -ErrorAction Stop

	Check 'target runs as the fixture account, not the driver' ((Fact 'IDENTITY') -like ('*\' + $FixtureUser)) ('identity=' + (Fact 'IDENTITY'))
	Check 'target SID differs from the driver SID' ((Fact 'SID') -ne ([Security.Principal.WindowsIdentity]::GetCurrent().User.Value)) ('target=' + (Fact 'SID'))

	$hostId = & (Join-Path $PSScriptRoot 'TestSupport\Start-DgSpyHost.ps1') -RpcPort $RpcPort; $RpcPort = [int]$env:DGSPY_RPC_PORT
	$program = Find-DgSpyProgram -ProcessId $targetPid -InvokeRpc ${function:Rpc}
	$attached = Rpc 'attach' @{ program_id = $program.program_id }; $sessionId = $attached.session_id
	if ($attached.state -eq 'paused') { $null = Rpc 'continue' @{ session_id = $sessionId } }

	# No app_domain_id: CoreCLR has one domain, so the precondition that refuses a multi-domain target
	# without a name must be satisfied here rather than refused.
	$readiness = Rpc 'get_hooklab_readiness' @{ session_id = $sessionId; process_id = $targetPid }
	Check 'readiness accepts a single-domain CoreCLR target with no domain named' (-not $readiness.refuses) ($readiness.summary)
	Check 'readiness reports the target as cross-identity' ((@($readiness.preconditions | Where-Object { $_.name -eq 'identity.target_readable' })[0].detail) -like '*different principals*')
	Check 'readiness reports a supported CoreCLR runtime' ((@($readiness.preconditions | Where-Object { $_.name -eq 'target.runtime_supported' })[0].result) -eq 'satisfied')
	Check 'readiness never claims the payload will load' ($readiness.summary -like '*no known incompatibility*' -and $readiness.summary -notlike '*loadable*') ($readiness.summary)
	Check 'readiness left the target unmodified' ((Rpc 'get_hooklab_status' @{ session_id = $sessionId; process_id = $targetPid }).initialized -eq $false)

	$initialized = Rpc 'initialize_hooklab' @{ session_id = $sessionId; process_id = $targetPid } 150
	Check 'HookLab initializes cross-identity on CoreCLR' ($initialized.initialized -and $initialized.state -eq 'ready') ($initialized | ConvertTo-Json -Compress)
	Check 'the target kept running through initialization' (Wait-Tick 20)

	$modules = @((Rpc 'list_modules' @{ session_id = $sessionId; name_pattern = 'HookLabCrossIdentityCoreTarget' }).modules)
	Check 'the target module is discoverable' (@($modules).Count -ge 1) ('modules=' + @($modules).Count)
	$module = @($modules)[0]
	$template = Rpc 'get_hook_template' @{ session_id = $sessionId; module_id = $module.module_id; method_token = [int](Fact 'WORKTOKEN'); template = 'Prefix' }
	$installed = Rpc 'install_hook' @{ session_id = $sessionId; process_id = $targetPid; hook_id = 'cross-identity-core-tick'; kind = 'Prefix'; module_id = $module.module_id; assembly = (Fact 'WORKASSEMBLY'); declaring_type = $template.target.declaring_type; method = $template.target.method; method_token = $template.target.method_token; signature = $template.target.signature; module_mvid = $template.target.module_mvid; il_sha256 = $template.target.il_sha256 } 90
	Check 'a guarded prefix installs cross-identity on CoreCLR' ($installed.installed) ($installed | ConvertTo-Json -Compress)

	$null = Wait-Tick 20
	$events = Rpc 'get_hook_events' @{ session_id = $sessionId; process_id = $targetPid; max_events = 32 } 90
	Check 'events arrive from the CoreCLR target' (@($events.events).Count -gt 0) ('events=' + @($events.events).Count)
	Check 'no events were dropped' ($events.dropped -eq 0)
	Check 'nothing is reported as shadowed' (@($events.shadowed_hooks).Count -eq 0)

	$removed = Rpc 'remove_hook' @{ session_id = $sessionId; process_id = $targetPid; hook_id = 'cross-identity-core-tick' } 90
	Check 'the hook is removed' ($removed.removed)
	Check 'the resident inventory is empty afterwards' ((@((Rpc 'list_hooks' @{ session_id = $sessionId; process_id = $targetPid }).hooks)).Count -eq 0)
	Check 'the target still runs after removal' (Wait-Tick 20)

	$null = Rpc 'detach' @{ session_id = $sessionId } 30; $sessionId = $null
	Check 'the target survives detach' (-not (Get-Process -Id $targetPid -ErrorAction SilentlyContinue).HasExited)
	Check 'the target still runs after detach' (Wait-Tick 20)
}
catch {
	$script:fail++
	$detail = ($_ | Out-String) + "`n" + ($_.ScriptStackTrace | Out-String)
	Write-Host ("FAIL  the smoke threw before completing: " + $_.Exception.Message) -ForegroundColor Red
	try { Set-Content -LiteralPath (Join-Path $root 'error.log') -Value $detail -Encoding utf8 } catch { }
}
finally {
	if ($sessionId) { try { $null = Rpc 'detach' @{ session_id = $sessionId } 30 } catch { } }
	foreach ($stray in @(Get-Process -Name 'HookLabCrossIdentityCoreTarget' -ErrorAction SilentlyContinue)) { try { Stop-Process -Id $stray.Id -Force -ErrorAction SilentlyContinue } catch { } }
	foreach ($id in @($hostId, $(if ($target) { $target.Id } else { 0 }))) { if ($id -gt 0) { Stop-Process -Id $id -Force -ErrorAction SilentlyContinue } }
	if ($stage -and (Test-Path $stage)) { try { Copy-Item $script:factsFile -Destination (Join-Path $root 'facts.txt') -Force -ErrorAction SilentlyContinue } catch { }; try { Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue } catch { } }
	Write-Host "Cross-identity CoreCLR HookLab smoke: $pass passed, $fail failed" -ForegroundColor Cyan
	Write-Host "logs: $root"
	try { Stop-Transcript | Out-Null } catch { }
}
if ($fail -ne 0) { exit 1 }
