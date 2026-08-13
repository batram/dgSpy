param(
	[ValidateSet('Pipeline','SharedGate','CorDebugGate')]
	[string]$Operation = 'Pipeline'
)

$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new()
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$feed = Join-Path $repoRoot '.nuget\offline-feed'
$manifestPath = Join-Path $feed 'manifest.json'
$offlineConfig = Join-Path $repoRoot '.nuget\offline.config'

if (-not (Test-Path -LiteralPath $manifestPath)) {
	throw "The offline NuGet feed is not prepared. Run .\tools\prepare-offline-nuget.ps1 once outside the sandbox."
}
$manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
foreach ($package in $manifest.packages) {
	$file = Join-Path $feed $package.name
	if (-not (Test-Path -LiteralPath $file) -or (Get-Item -LiteralPath $file).Length -ne $package.length) {
		throw "The offline NuGet feed is incomplete. Run .\tools\prepare-offline-nuget.ps1 outside the sandbox. Missing or invalid: $($package.name)"
	}
}

$previousRestoreConfig = $env:RestoreConfigFile
$env:RestoreConfigFile = [IO.Path]::GetFullPath($offlineConfig)
Push-Location $repoRoot
try {
	switch ($Operation) {
		'Pipeline' { dotnet run --project Build\DgSpyTool -- pipeline }
		'SharedGate' { powershell -NoProfile -File tests\run-modernization-gate.ps1 -Stage Shared }
		'CorDebugGate' { powershell -NoProfile -File tests\run-modernization-gate.ps1 -Stage CorDebug }
	}
	if ($LASTEXITCODE) { exit $LASTEXITCODE }
}
finally {
	if ($null -eq $previousRestoreConfig) { Remove-Item Env:RestoreConfigFile -ErrorAction SilentlyContinue }
	else { $env:RestoreConfigFile = $previousRestoreConfig }
	Pop-Location
}
