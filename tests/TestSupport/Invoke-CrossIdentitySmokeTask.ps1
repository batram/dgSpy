#requires -Version 5.1
<#
Runs one cross-identity smoke on behalf of a scheduled task. ASCII-only.

The task exists so an UNELEVATED gate can still run legs that need SeDebugPrivilege: debugging a
process owned by another account requires it, and a standard token does not have it. Registering the
task takes elevation once (Register-CrossIdentitySmokeTasks.ps1); triggering it afterwards does not,
so the gate stays non-interactive.

It is deliberately not the smoke itself. The task's action is fixed at registration time, while the
layout root and run directory change per gate run, so those arrive through a request file and the
result goes back through a result file. Anything else - passing them as task arguments, or reading the
gate's environment - would need the task re-registered for every run, which is the interaction this
removes.
#>
[CmdletBinding()]
param([Parameter(Mandatory = $true)][ValidateSet('net48', 'coreclr')][string]$Which)
$ErrorActionPreference = 'Stop'
$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new()

# Best-effort fallback for tasks registered before the no-console WScript launcher was added. Task
# Scheduler starts those old PowerShell actions in the INTERACTIVE session, where their console can
# flash before this code runs. Re-registering the tasks prevents the console from being created; this
# block still hides an old task as soon as its script begins.
try {
	Add-Type -Name Window -Namespace Native -MemberDefinition '
		[DllImport("kernel32.dll")] public static extern IntPtr GetConsoleWindow();
		[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr handle, int command);' -ErrorAction Stop
	$console = [Native.Window]::GetConsoleWindow()
	if ($console -ne [IntPtr]::Zero) { [void][Native.Window]::ShowWindow($console, 0) }  # SW_HIDE
}
catch { }

$exchange = Join-Path $env:ProgramData 'dgSpy\fixture'
$requestPath = Join-Path $exchange ("request-$Which.json")

# Into the run directory the caller created, not the shared exchange: this task runs elevated and the
# caller does not, so a result file written here would be owned by an administrator token and the
# caller could not replace it on the next run. Measured - the second run failed with "Access to the
# path is denied" before it started anything.
function Write-Result([int]$Code, [string]$Detail, [string]$RunDirectory) {
	$payload = [ordered]@{ which = $Which; exit_code = $Code; detail = $Detail; run_directory = $RunDirectory; completed_utc = [DateTime]::UtcNow.ToString('o') }
	if (-not $RunDirectory) { return }
	New-Item -ItemType Directory -Force -Path $RunDirectory | Out-Null
	Set-Content -LiteralPath (Join-Path $RunDirectory 'task-result.json') -Value ($payload | ConvertTo-Json) -Encoding utf8
}

try {
	if (-not (Test-Path $requestPath)) { throw "No request file at $requestPath. The gate writes one before triggering this task." }
	$request = Get-Content -LiteralPath $requestPath -Raw | ConvertFrom-Json
	$repoRoot = [string]$request.repo_root
	$env:DGSPY_LAYOUT_ROOT = [string]$request.layout_root
	$runDirectory = [string]$request.run_directory
	$script = if ($Which -eq 'net48') { 'run-hooklab-cross-identity-smoke.ps1' } else { 'run-hooklab-cross-identity-coreclr-smoke.ps1' }
	$smoke = Join-Path $repoRoot ('tests\' + $script)

	# A scheduled task runs on the interactive DESKTOP, so dnSpy would appear and take focus on every
	# gate run - the thing hidden-desktop tooling exists to prevent. If the machine has such a launcher,
	# the request names it and the smoke goes there instead. Optional and passed in rather than
	# hardcoded: the launcher is personal tooling, not part of this repository.
	$launcher = [string]$request.hidden_desktop_launcher
	if ($launcher -and (Test-Path $launcher)) {
		$taskLog = Join-Path $runDirectory 'hidden-desktop.log'
		New-Item -ItemType Directory -Force -Path $runDirectory | Out-Null
		# Captured, NOT redirected with *>. The launcher returns its exit code on the pipeline, so
		# redirecting every stream to a file sends the code to the file too and leaves nothing to read -
		# measured here, on a run whose smoke was 20/20 green and which this reported as a failure. The
		# code is the last integer the launcher emits; everything else goes to the log.
		$outcome = & $launcher -FilePath "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" `
			-ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $smoke, '-RunDirectory', $runDirectory `
			-WorkingDirectory $repoRoot -TimeoutSeconds 900 2>&1
		Set-Content -LiteralPath $taskLog -Value ($outcome | Out-String) -Encoding utf8
		$code = @($outcome | Where-Object { $_ -is [int] })
		# An undetermined code must never read as success.
		if ($code.Count -gt 0) { Write-Result ([int]$code[-1]) 'completed on the hidden desktop' $runDirectory }
		else { Write-Result 1 ("the hidden-desktop launcher returned no exit code; see " + $taskLog) $runDirectory }
		return
	}

	& $smoke -RunDirectory $runDirectory
	Write-Result $LASTEXITCODE 'completed' $runDirectory
}
catch {
	# A task that fails without saying why is worse than one that does not run: the gate can only see
	# what lands in the result file.
	Write-Result 1 (($_ | Out-String).Trim()) $(if ($runDirectory) { $runDirectory } else { '' })
	exit 1
}
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
