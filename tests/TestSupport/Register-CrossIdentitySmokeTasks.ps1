#requires -Version 5.1
<#
One-time, elevated setup for the cross-identity smokes. ASCII-only. Run once per machine.

  powershell -NoProfile -ExecutionPolicy Bypass -File tests\TestSupport\Register-CrossIdentitySmokeTasks.ps1

After this, the modernization gate runs both cross-identity legs with no prompt and no interaction,
whether or not the gate itself is elevated.

WHY A TASK. Debugging a process owned by another account requires SeDebugPrivilege, which a standard
token does not carry - a non-elevated dnSpy cannot even enumerate the target. The alternatives were
worse:

  - requiring every gate run to be started elevated, which is interaction by another name;
  - granting SeDebugPrivilege to the developer's own account, which is permanent and
    admin-equivalent, to make a test suite convenient.

A scheduled task registered once with highest privileges costs one elevated command and leaves no
standing grant on the interactive account.

IT RUNS IN YOUR SESSION, NOT AS SYSTEM. dnSpy needs a real desktop: a SYSTEM task puts it in session 0,
where it was measured starting and then exiting on its own with nothing in the event log. So the task
is registered for the current user, "run only when this user is logged on", with highest privileges.

WHAT IT CREATES
  - the local account dgspy-fixture (Users group only, fixed public password) if absent;
  - two scheduled tasks under \dgSpy\, one per runtime.

To remove everything:
  Unregister-ScheduledTask -TaskPath '\dgSpy\' -TaskName 'cross-identity-net48','cross-identity-coreclr' -Confirm:$false
  Remove-LocalUser -Name dgspy-fixture
#>
[CmdletBinding()]
param(
	[string]$FixtureUser = 'dgspy-fixture',
	[string]$FixturePassword = 'DgSpyFixture!2026'
)
$ErrorActionPreference = 'Stop'
$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new()

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
	throw 'Run this once from an elevated shell. It creates a local account and registers scheduled tasks; both need administrator rights. Everything afterwards does not.'
}

$repoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$runner = Join-Path $PSScriptRoot 'Invoke-CrossIdentitySmokeTask.ps1'
if (-not (Test-Path $runner)) { throw "Missing $runner" }

# The account. Idempotent, because this script is the documented way to get a machine into shape and
# will be run again on machines that are already half-configured.
$existing = Get-LocalUser -Name $FixtureUser -ErrorAction SilentlyContinue
if (-not $existing) {
	Write-Host "Creating local account $FixtureUser (Users group only)" -ForegroundColor Cyan
	New-LocalUser -Name $FixtureUser -Password (ConvertTo-SecureString $FixturePassword -AsPlainText -Force) `
		-FullName 'dgSpy test fixture' -Description 'Non-admin account for dgSpy cross-identity tests' `
		-PasswordNeverExpires -UserMayNotChangePassword | Out-Null
	Add-LocalGroupMember -Group Users -Member $FixtureUser
}
else {
	Add-Type -AssemblyName System.DirectoryServices.AccountManagement
	$context = New-Object System.DirectoryServices.AccountManagement.PrincipalContext([System.DirectoryServices.AccountManagement.ContextType]::Machine)
	if (-not $context.ValidateCredentials($FixtureUser, $FixturePassword)) {
		Write-Host "Resetting the password of the existing $FixtureUser account to the fixture value" -ForegroundColor Yellow
		Set-LocalUser -Name $FixtureUser -Password (ConvertTo-SecureString $FixturePassword -AsPlainText -Force)
	}
	else { Write-Host "Account $FixtureUser already exists with the fixture password" -ForegroundColor DarkGray }
}

# The exchange directory the task and the gate pass files through. Machine-wide, because the gate may
# run as one account and the task as another.
$exchange = Join-Path $env:ProgramData 'dgSpy\fixture'
New-Item -ItemType Directory -Force -Path $exchange | Out-Null

$principalUser = [Security.Principal.WindowsIdentity]::GetCurrent().Name
foreach ($leg in @(@{ Name = 'cross-identity-net48'; Which = 'net48' }, @{ Name = 'cross-identity-coreclr'; Which = 'coreclr' })) {
	$action = New-ScheduledTaskAction -Execute "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" `
		-Argument ('-NoProfile -ExecutionPolicy Bypass -File "' + $runner + '" -Which ' + $leg.Which) `
		-WorkingDirectory $repoRoot
	# Interactive so dnSpy gets a desktop; highest privileges so the smoke has SeDebugPrivilege.
	$principal = New-ScheduledTaskPrincipal -UserId $principalUser -LogonType Interactive -RunLevel Highest
	$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Minutes 20) -MultipleInstances IgnoreNew
	Register-ScheduledTask -TaskPath '\dgSpy\' -TaskName $leg.Name -Action $action -Principal $principal -Settings $settings -Force | Out-Null
	Write-Host ("Registered \dgSpy\" + $leg.Name) -ForegroundColor Green
}

Write-Host ''
Write-Host 'Done. The gate will now run both cross-identity legs without elevation or prompts.' -ForegroundColor Cyan
Write-Host 'Verify with: Get-ScheduledTask -TaskPath \dgSpy\' -ForegroundColor DarkGray
