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
$exchange = Join-Path $env:ProgramData 'dgSpy\fixture'
$requestPath = Join-Path $exchange ("request-$Which.json")
$resultPath = Join-Path $exchange ("result-$Which.json")

function Write-Result([int]$Code, [string]$Detail, [string]$RunDirectory) {
	$payload = [ordered]@{ which = $Which; exit_code = $Code; detail = $Detail; run_directory = $RunDirectory; completed_utc = [DateTime]::UtcNow.ToString('o') }
	Set-Content -LiteralPath $resultPath -Value ($payload | ConvertTo-Json) -Encoding utf8
}

try {
	if (Test-Path $resultPath) { Remove-Item $resultPath -Force }
	if (-not (Test-Path $requestPath)) { throw "No request file at $requestPath. The gate writes one before triggering this task." }
	$request = Get-Content -LiteralPath $requestPath -Raw | ConvertFrom-Json
	$repoRoot = [string]$request.repo_root
	$env:DGSPY_LAYOUT_ROOT = [string]$request.layout_root
	$runDirectory = [string]$request.run_directory
	$script = if ($Which -eq 'net48') { 'run-hooklab-cross-identity-smoke.ps1' } else { 'run-hooklab-cross-identity-coreclr-smoke.ps1' }

	& (Join-Path $repoRoot ('tests\' + $script)) -RunDirectory $runDirectory
	Write-Result $LASTEXITCODE 'completed' $runDirectory
}
catch {
	# A task that fails without saying why is worse than one that does not run: the gate can only see
	# what lands in the result file.
	Write-Result 1 (($_ | Out-String).Trim()) $(if ($runDirectory) { $runDirectory } else { '' })
	exit 1
}
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
