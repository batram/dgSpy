param(
	[ValidateSet('delayed-ready', 'never-ready')]
	[string]$Mode = 'delayed-ready',
	[string]$ReportPath,
	[ValidateRange(25, 2200)]
	[int]$ReadinessDelayMilliseconds = 750,
	[ValidateRange(1, 100)]
	[int]$Repeat = 1
)
$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new()
$root = Split-Path $PSScriptRoot -Parent
$executable = Join-Path $root 'tests\TestTargets\HookLab.VmConnectLifecycleTarget\bin\Release\net48\HookLab.VmConnectLifecycleTarget.exe'
if ([string]::IsNullOrWhiteSpace($ReportPath)) { $ReportPath = Join-Path $env:TEMP 'HookLab.VmConnectLifecycleTarget.report.txt' }
$failed = 0
for ($run = 1; $run -le $Repeat; $run++) {
	$currentReport = if ($Repeat -eq 1) { $ReportPath } else { $ReportPath + '.' + $run }
	$childResult = & C:\Users\mjb\tools\agent-scripts\Invoke-OnHiddenDesktop.ps1 -FilePath $executable -ArgumentList @($currentReport, $Mode, $ReadinessDelayMilliseconds) -WorkingDirectory $root -TimeoutSeconds 30
	if ([int]$childResult -ne 0) { $failed++ }
}
Write-Output ("mode={0} runs={1} failed={2}" -f $Mode, $Repeat, $failed)
if ($failed -ne 0) { exit 1 }
