#requires -Version 5.1
<#
Cross-identity, cross-domain HookLab lifecycle smoke. ASCII-only; run on a hidden desktop.

Target environment contract, subslice 8. Every other HookLab gate runs the debugger and the target as the same user, in the
default application domain, on a developer machine - an environment that silently satisfies every
assumption the 2026-08-19 incident violated. Five defects hid behind that arrangement, and three more
were found by hand against a live IIS worker on 2026-08-20 because no gate could reach them:

  - a payload parameter the resident refused, surfacing only as a twenty-second timeout;
  - a provenance guard that refused a hosted application's own assemblies;
  - an exchange-area DACL that let the target write its report and not publish it.

This runs the target as a SECOND LOCAL ACCOUNT and hooks a method that exists only in a SECOND
APPLICATION DOMAIN, so those axes are exercised by the gate rather than by a lab VM that has to be
available.

The account is a fixture, not a secret: create it once, elevated, with

  New-LocalUser -Name dgspy-fixture -Password (ConvertTo-SecureString 'DgSpyFixture!2026' -AsPlainText -Force) -PasswordNeverExpires -UserMayNotChangePassword
  Add-LocalGroupMember -Group Users -Member dgspy-fixture

It is a member of Users only. If it is absent this script says so and exits non-zero rather than
quietly degrading to a same-user run, which would be the failure mode it exists to remove.
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
$targetDir = Join-Path $repoRoot 'tests\TestTargets\HookLabCrossIdentityTarget\bin\Release\net48'
$root = if ($RunDirectory) { $RunDirectory } else { Join-Path $PSScriptRoot ('artifacts\cross-identity-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
$hostId = 0; $target = $null; $sessionId = $null; $pass = 0; $fail = 0; $stage = $null
. (Join-Path $PSScriptRoot 'TestSupport\Invoke-DgSpyRpc.ps1')
. (Join-Path $PSScriptRoot 'TestSupport\Find-DgSpyProgram.ps1')
function Rpc([string]$Operation, [hashtable]$Arguments = @{}, [int]$Deadline = 70) { Invoke-DgSpyRpc -OperationName $Operation -OperationArguments $Arguments -RpcPort $RpcPort -DeadlineSeconds $Deadline }
function Check([string]$Name, $Condition, [string]$Detail = '') { if ([bool]$Condition) { $script:pass++; Write-Host "PASS  $Name" -ForegroundColor DarkGreen } else { $script:fail++; Write-Host "FAIL  $Name  $Detail" -ForegroundColor Red } }
function Lines { @(Get-Content -LiteralPath $script:factsFile -ErrorAction SilentlyContinue) }
function Fact([string]$Name) { ((Lines | Where-Object { $_ -like "$Name *" } | Select-Object -First 1) -replace ('^' + $Name + ' '), '') }
# Two matchers, because the target writes both shapes and conflating them cost the first run: Fact
# reads "NAME value" lines, and READY is a bare marker with no value, so a "READY *" match never fires.
function Wait-Fact([string]$Name, [int]$Seconds = 30) { $deadline = [DateTime]::UtcNow.AddSeconds($Seconds); do { Start-Sleep -Milliseconds 200; if (Fact $Name) { return $true } } until ([DateTime]::UtcNow -gt $deadline); return $false }
function Wait-Marker([string]$Text, [int]$Seconds = 30) { $deadline = [DateTime]::UtcNow.AddSeconds($Seconds); do { Start-Sleep -Milliseconds 200; if (@(Lines | Where-Object { $_ -eq $Text }).Count) { return $true } } until ([DateTime]::UtcNow -gt $deadline); return $false }
function Wait-Tick([int]$Seconds = 15) { $before = @(Lines | Where-Object { $_ -like 'TICK *' }).Count; $deadline = [DateTime]::UtcNow.AddSeconds($Seconds); do { Start-Sleep -Milliseconds 200; if (@(Lines | Where-Object { $_ -like 'TICK *' }).Count -gt $before) { return $true } } until ([DateTime]::UtcNow -gt $deadline); return $false }

try {
	New-Item -ItemType Directory -Force -Path $root | Out-Null
	Start-Transcript -LiteralPath (Join-Path $root 'driver.log') -Force | Out-Null

	if (-not (Get-LocalUser -Name $FixtureUser -ErrorAction SilentlyContinue)) {
		throw "The fixture account '$FixtureUser' does not exist. Create it elevated (see the header of this script). This smoke refuses to fall back to a same-user run: a cross-identity gate that silently runs as one identity is the gap it exists to close."
	}
	# Debugging a process owned by another account needs SeDebugPrivilege, which a standard token does
	# not carry: a non-elevated dnSpy cannot even enumerate the target, and the failure surfaces late and
	# obscurely as "pid N was not discoverable" while the process is plainly alive. Measured here on the
	# first run that got that far. This is the same reason the 2026-08-19 incident ran its host as
	# Administrator against a service-account worker - it is a property of the scenario, not a defect, so
	# the smoke states it before it stages or launches anything.
	if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
		throw "This smoke must run elevated. It debugs a process owned by '$FixtureUser', and attaching across accounts requires SeDebugPrivilege, which a standard token does not have - dnSpy cannot even list the target without it. Run it from an elevated shell."
	}
	Add-Type -AssemblyName System.DirectoryServices.AccountManagement
	$context = New-Object System.DirectoryServices.AccountManagement.PrincipalContext([System.DirectoryServices.AccountManagement.ContextType]::Machine)
	if (-not $context.ValidateCredentials($FixtureUser, $FixturePassword)) {
		throw "The fixture account '$FixtureUser' exists but its password does not match. Reset it elevated: Set-LocalUser -Name $FixtureUser -Password (ConvertTo-SecureString '$FixturePassword' -AsPlainText -Force)"
	}
	if (-not (Test-Path (Join-Path $targetDir 'HookLabCrossIdentityTarget.exe'))) {
		throw "Build the fixture first: dotnet build tests\TestTargets\HookLabCrossIdentityTarget\HookLabCrossIdentityTarget.csproj -c Release"
	}
	# The gate sets this after its pipeline build; a standalone run has to be told. Named here rather
	# than left to Start-DgSpyHost's generic complaint, which says what is wrong and not what to do.
	if (-not $env:DGSPY_LAYOUT_ROOT) {
		throw "DGSPY_LAYOUT_ROOT is not set. Point it at a completed layout, for example: `$env:DGSPY_LAYOUT_ROOT = '$repoRoot\artifacts\layouts\<build-id>'. The modernization gate sets this for you; a standalone run does not."
	}

	# A machine-wide staging directory, because neither principal's profile is reachable by the other -
	# which is defect 3 of the incident, in the fixture's own setup rather than in the product.
	$stage = Join-Path $env:ProgramData ('dgSpy\fixture\' + [Guid]::NewGuid().ToString('N'))
	New-Item -ItemType Directory -Force -Path $stage | Out-Null
	Copy-Item (Join-Path $targetDir '*') -Destination $stage -Recurse -Force
	$script:factsFile = Join-Path $stage 'facts.txt'
	New-Item -ItemType File -Path $script:factsFile -Force | Out-Null
	# Read and execute for the target account, and write ONLY on its facts file. The account never needs
	# to modify the code it runs.
	& icacls $stage /grant ("$FixtureUser" + ':(OI)(CI)(RX)') | Out-Null
	& icacls $script:factsFile /grant ("$FixtureUser" + ':(M)') | Out-Null

	$credential = New-Object System.Management.Automation.PSCredential($FixtureUser, (ConvertTo-SecureString $FixturePassword -AsPlainText -Force))
	Start-Process -FilePath (Join-Path $stage 'HookLabCrossIdentityTarget.exe') -ArgumentList $script:factsFile -Credential $credential -WorkingDirectory $stage -WindowStyle Hidden
	if (-not (Wait-Marker 'READY' 60)) { throw ('The cross-identity target did not become ready. Facts: ' + ((Lines) -join ' | ')) }
	$targetPid = [int](Fact 'PROCESS')
	$target = Get-Process -Id $targetPid -ErrorAction Stop

	Check 'target runs as the fixture account, not the driver' ((Fact 'IDENTITY') -like ('*\' + $FixtureUser)) ('identity=' + (Fact 'IDENTITY'))
	Check 'target SID differs from the driver SID' ((Fact 'SID') -ne ([Security.Principal.WindowsIdentity]::GetCurrent().User.Value)) ('target=' + (Fact 'SID'))
	$workDomain = [int](Fact 'WORKDOMAIN')
	Check 'hookable code runs in a secondary application domain' ($workDomain -gt 1) ('work domain=' + $workDomain)

	$hostId = & (Join-Path $PSScriptRoot 'TestSupport\Start-DgSpyHost.ps1') -RpcPort $RpcPort; $RpcPort = [int]$env:DGSPY_RPC_PORT
	$program = Find-DgSpyProgram -ProcessId $targetPid -InvokeRpc ${function:Rpc}
	$attached = Rpc 'attach' @{ program_id = $program.program_id }; $sessionId = $attached.session_id
	if ($attached.state -eq 'paused') { $null = Rpc 'continue' @{ session_id = $sessionId } }

	# The contract answers before anything is injected, and it is asked about BOTH domains.
	$readinessNoDomain = Rpc 'get_hooklab_readiness' @{ session_id = $sessionId; process_id = $targetPid }
	Check 'readiness refuses a multi-domain target that names no domain' ($readinessNoDomain.refuses -and $readinessNoDomain.refusal.code -eq 'hooklab_application_domain_required') ($readinessNoDomain.summary)
	Check 'readiness reports the target as cross-identity' ((@($readinessNoDomain.preconditions | Where-Object { $_.name -eq 'identity.target_readable' })[0].detail) -like '*different principals*')
	$readiness = Rpc 'get_hooklab_readiness' @{ session_id = $sessionId; process_id = $targetPid; app_domain_id = $workDomain }
	Check 'readiness accepts the named application domain' (-not $readiness.refuses) ($readiness.summary)
	Check 'readiness never claims the payload will load' ($readiness.summary -like '*no known incompatibility*' -and $readiness.summary -notlike '*loadable*') ($readiness.summary)
	Check 'readiness left the target unmodified' ((Rpc 'get_hooklab_status' @{ session_id = $sessionId; process_id = $targetPid }).initialized -eq $false)

	$initialized = Rpc 'initialize_hooklab' @{ session_id = $sessionId; process_id = $targetPid; app_domain_id = $workDomain } 150
	Check 'HookLab initializes cross-identity in the application domain' ($initialized.initialized -and $initialized.state -eq 'ready') ($initialized | ConvertTo-Json -Compress)
	Check 'the target kept running through initialization' (Wait-Tick 20)

	$modules = @((Rpc 'list_modules' @{ session_id = $sessionId; name_pattern = 'HookLabCrossIdentityWork' }).modules)
	Check 'the work assembly is visible only in the application domain' ((@($modules | Where-Object { $_.app_domain_id -eq $workDomain }).Count -eq 1) -and (@($modules | Where-Object { $_.app_domain_id -eq 1 }).Count -eq 0)) (($modules | ForEach-Object { $_.app_domain_id }) -join ',')
	$module = @($modules | Where-Object { $_.app_domain_id -eq $workDomain })[0]

	$template = Rpc 'get_hook_template' @{ session_id = $sessionId; module_id = $module.module_id; method_token = [int](Fact 'WORKTOKEN'); template = 'Prefix' }
	$installed = Rpc 'install_hook' @{ session_id = $sessionId; process_id = $targetPid; hook_id = 'cross-identity-tick'; kind = 'Prefix'; module_id = $module.module_id; assembly = (Fact 'WORKASSEMBLY'); declaring_type = $template.target.declaring_type; method = $template.target.method; method_token = $template.target.method_token; signature = $template.target.signature; module_mvid = $template.target.module_mvid; il_sha256 = $template.target.il_sha256 } 90
	Check 'a guarded prefix installs on application-domain code' ($installed.installed) ($installed | ConvertTo-Json -Compress)

	$null = Wait-Tick 20
	$events = Rpc 'get_hook_events' @{ session_id = $sessionId; process_id = $targetPid; max_events = 32 } 90
	Check 'events arrive from the application domain' (@($events.events).Count -gt 0) ('events=' + @($events.events).Count)
	Check 'no events were dropped' ($events.dropped -eq 0)
	Check 'nothing is reported as shadowed' (@($events.shadowed_hooks).Count -eq 0)

	$removed = Rpc 'remove_hook' @{ session_id = $sessionId; process_id = $targetPid; hook_id = 'cross-identity-tick' } 90
	Check 'the hook is removed' ($removed.removed)
	Check 'the resident inventory is empty afterwards' ((@((Rpc 'list_hooks' @{ session_id = $sessionId; process_id = $targetPid }).hooks)).Count -eq 0)
	Check 'the target still runs after removal' (Wait-Tick 20)

	$null = Rpc 'detach' @{ session_id = $sessionId } 30; $sessionId = $null
	Check 'the target survives detach' (-not (Get-Process -Id $targetPid -ErrorAction SilentlyContinue).HasExited)
	Check 'the target still runs after detach' (Wait-Tick 20)
}
catch {
	# Written where it can be read. The smoke runs on a hidden desktop, whose stderr nobody sees, so an
	# exception before the first Check otherwise surfaces as "0 passed, 0 failed" and nothing else -
	# which is how the first run of this script reported a failure it had fully diagnosed internally.
	$script:fail++
	$detail = ($_ | Out-String) + "`n" + ($_.ScriptStackTrace | Out-String)
	Write-Host ("FAIL  the smoke threw before completing: " + $_.Exception.Message) -ForegroundColor Red
	try { Set-Content -LiteralPath (Join-Path $root 'error.log') -Value $detail -Encoding utf8 } catch { }
}
finally {
	if ($sessionId) { try { $null = Rpc 'detach' @{ session_id = $sessionId } 30 } catch { } }
	# By name as well as by PID: a throw before $target is assigned would otherwise leave the fixture
	# account's process running forever, and it is launched detached from this shell.
	foreach ($stray in @(Get-Process -Name 'HookLabCrossIdentityTarget' -ErrorAction SilentlyContinue)) { try { Stop-Process -Id $stray.Id -Force -ErrorAction SilentlyContinue } catch { } }
	foreach ($id in @($hostId, $(if ($target) { $target.Id } else { 0 }))) { if ($id -gt 0) { Stop-Process -Id $id -Force -ErrorAction SilentlyContinue } }
	if ($stage -and (Test-Path $stage)) { try { Copy-Item $script:factsFile -Destination (Join-Path $root 'facts.txt') -Force -ErrorAction SilentlyContinue } catch { }; try { Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue } catch { } }
	Write-Host "Cross-identity HookLab smoke: $pass passed, $fail failed" -ForegroundColor Cyan
	Write-Host "logs: $root"
	try { Stop-Transcript | Out-Null } catch { }
}
if ($fail -ne 0) { exit 1 }
