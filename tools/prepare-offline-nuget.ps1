param([switch]$SkipProof)

$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new()
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$nugetRoot = Join-Path $repoRoot '.nuget'
$feed = Join-Path $nugetRoot 'offline-feed'
$staging = Join-Path $nugetRoot ('.offline-feed.staging-' + [Guid]::NewGuid().ToString('N'))
$onlineSource = 'https://api.nuget.org/v3/index.json'
$projects = @(
	'dnSpy.sln',
	'Build\DgSpy.Components.proj',
	'tests\DgSpyTool.Tests\DgSpyTool.Tests.csproj',
	'tests\dgSpy.Protocol.Tests\dgSpy.Protocol.Tests.csproj',
	'tests\dgSpy.Contracts.Tests\dgSpy.Contracts.Tests.csproj',
	'tests\HookLab.Probe.Tests\HookLab.Probe.Tests.csproj',
	'tests\HookLab.Transport.Tests\HookLab.Transport.Tests.csproj',
	'tests\HookLab.Bootstrap.Tests\HookLab.Bootstrap.Tests.csproj',
	'tests\dgSpy.Gateway.Tests\dgSpy.Gateway.Tests.csproj',
	'tests\dgSpy.Extension.Tests\dgSpy.Extension.Tests.csproj',
	'tests\dgSpy.Composition.Tests\dgSpy.Composition.Tests.csproj'
)

function Invoke-CheckedRestore {
	param([string]$Project)
	Write-Host "Restoring $Project from nuget.org"
	if ($Project.EndsWith('.proj', [StringComparison]::OrdinalIgnoreCase)) {
		dotnet msbuild $Project /t:Restore "/p:RestoreSources=$onlineSource" /p:NuGetAudit=true /nologo /v:minimal
	} else {
		dotnet restore $Project --source $onlineSource -p:NuGetAudit=true --nologo --verbosity minimal
	}
	if ($LASTEXITCODE) { throw "Online restore failed for $Project with exit code $LASTEXITCODE" }
}

Push-Location $repoRoot
$previousRestoreConfig = $env:RestoreConfigFile
$previousNuGetPackages = $env:NUGET_PACKAGES
try {
	Remove-Item Env:RestoreConfigFile -ErrorAction SilentlyContinue
	foreach ($project in $projects) { Invoke-CheckedRestore $project }

	$packagePaths = [System.Collections.Generic.SortedSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
	$assetsFiles = Get-ChildItem -LiteralPath $repoRoot -Recurse -Filter project.assets.json -File |
		Where-Object { $_.FullName -notlike '*\artifacts\*' }
	foreach ($assetsFile in $assetsFiles) {
		$assets = Get-Content -Raw -LiteralPath $assetsFile.FullName | ConvertFrom-Json
		foreach ($library in $assets.libraries.PSObject.Properties) {
			if ($library.Value.type -eq 'package') { [void]$packagePaths.Add($library.Value.path) }
		}
	}

	# SDK-selected packs can be satisfied by an installed SDK and omitted from the
	# package library list even though a cold global package folder needs them.
	$runtimePackVersion = '10.0.10'
	foreach ($runtimePack in @(
		'microsoft.netcore.app.runtime.win-x64',
		'microsoft.windowsdesktop.app.runtime.win-x64',
		'microsoft.aspnetcore.app.runtime.win-x64',
		'microsoft.netcore.app.runtime.win-x86',
		'microsoft.windowsdesktop.app.runtime.win-x86',
		'microsoft.aspnetcore.app.runtime.win-x86',
		'microsoft.netcore.app.host.win-x86'
	)) { [void]$packagePaths.Add("$runtimePack/$runtimePackVersion") }
	foreach ($targetingPack in @(
		'microsoft.netcore.app.ref',
		'microsoft.windowsdesktop.app.ref',
		'microsoft.aspnetcore.app.ref'
	)) { [void]$packagePaths.Add("$targetingPack/7.0.20") }

	$globalPackagesLine = dotnet nuget locals global-packages --list
	if ($LASTEXITCODE) { throw 'Could not locate the NuGet global package folder.' }
	$globalPackages = ($globalPackagesLine -replace '^global-packages:\s*', '').TrimEnd('\')
	New-Item -ItemType Directory -Force -Path $staging | Out-Null
	foreach ($packagePath in $packagePaths) {
		$parts = $packagePath -split '/'
		$archiveName = $parts[0].ToLowerInvariant() + '.' + $parts[1] + '.nupkg'
		$archive = Join-Path (Join-Path $globalPackages ($packagePath -replace '/', '\')) $archiveName
		if (-not (Test-Path -LiteralPath $archive)) { throw "Missing restored package archive: $archive" }
		Copy-Item -LiteralPath $archive -Destination (Join-Path $staging $archiveName)
	}

	$manifest = Get-ChildItem -LiteralPath $staging -Filter *.nupkg -File | Sort-Object Name | ForEach-Object {
		[ordered]@{ name = $_.Name; length = $_.Length }
	}
	[ordered]@{ format = 1; packages = @($manifest) } |
		ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $staging 'manifest.json') -Encoding UTF8
	if (Test-Path -LiteralPath $feed) { Remove-Item -LiteralPath $feed -Recurse -Force }
	Move-Item -LiteralPath $staging -Destination $feed

	if (-not $SkipProof) {
		$proofPackages = Join-Path $nugetRoot ('.proof-packages-' + [Guid]::NewGuid().ToString('N'))
		$env:NUGET_PACKAGES = $proofPackages
		try {
			Write-Host 'Proving the feed with an empty global package folder and HTTP cache disabled'
			dotnet restore Build\DgSpyTool\DgSpyTool.csproj --configfile .nuget\offline.config --force --no-http-cache --nologo --verbosity minimal
			if ($LASTEXITCODE) { throw "Cold offline proof failed with exit code $LASTEXITCODE" }
		} finally {
			Remove-Item Env:NUGET_PACKAGES -ErrorAction SilentlyContinue
			Remove-Item -LiteralPath $proofPackages -Recurse -Force -ErrorAction SilentlyContinue
		}
	}

	$bytes = 0L
	foreach ($entry in $manifest) { $bytes += [long]$entry.length }
	Write-Host "Prepared $($manifest.Count) packages ($bytes bytes) in $feed"
}
finally {
	if ($null -eq $previousRestoreConfig) { Remove-Item Env:RestoreConfigFile -ErrorAction SilentlyContinue }
	else { $env:RestoreConfigFile = $previousRestoreConfig }
	if ($null -eq $previousNuGetPackages) { Remove-Item Env:NUGET_PACKAGES -ErrorAction SilentlyContinue }
	else { $env:NUGET_PACKAGES = $previousNuGetPackages }
	if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
	Pop-Location
}
