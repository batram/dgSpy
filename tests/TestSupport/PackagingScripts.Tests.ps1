$ErrorActionPreference = 'Stop'
$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new()

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$packPath = Join-Path $repoRoot 'pack-dgspy.ps1'
$installPath = Join-Path $repoRoot 'install-dgspy.ps1'
$layoutTestPath = Join-Path $repoRoot 'tools\Test-DgSpyPackagedHostLayout.ps1'
$poisonedRootFixture = Join-Path $PSScriptRoot 'Fixtures\poisoned-net48-root-files.txt'

function Read-ScriptAst([string]$ScriptPath) {
	$tokens = $null
	$errors = $null
	$ast = [Management.Automation.Language.Parser]::ParseFile($ScriptPath, [ref]$tokens, [ref]$errors)
	if ($errors.Count -ne 0) { throw "$ScriptPath has PowerShell parse errors: $($errors.Message -join '; ')" }
	return $ast
}

$packAst = Read-ScriptAst $packPath
$installAst = Read-ScriptAst $installPath
$packText = $packAst.Extent.Text
$installText = $installAst.Extent.Text

$compressionParameter = $packAst.ParamBlock.Parameters |
	Where-Object { $_.Name.VariablePath.UserPath -eq 'CompressionLevel' }
if ($null -eq $compressionParameter -or $compressionParameter.DefaultValue.Extent.Text -ne "'Optimal'") {
	throw 'Release packaging must default to Optimal compression.'
}
if ($packText -notmatch 'Join-Path\s+\$resolved\s+"dgspy-\$Runtime\.zip"' -or
	$packText -notmatch 'ZipFile\]::CreateFromDirectory') {
	throw 'The default packer must retain the portable dgspy-$Runtime.zip output.'
}
if ($packText -notmatch 'if\s*\(\$DirectoryPackage\)' -or
	$packText -notmatch 'Join-Path\s+\$resolved\s+"dgspy-\$Runtime"') {
	throw 'DirectoryPackage must publish the complete unarchived package beside release artifacts.'
}
if ($installText -notmatch '&\s+\$packScript\s+-OutputDirectory\s+\$localPackageDirectory\s+-DirectoryPackage' -or
	$installText -notmatch "artifacts\\dgspy-local") {
	throw 'Repository installs must request the isolated directory package, not overwrite release output.'
}
if ($installText -notmatch 'Test-Path\s+-LiteralPath\s+\$resolvedPackage\s+-PathType\s+Container' -or
	$installText -notmatch 'Test-Path\s+-LiteralPath\s+\$resolvedPackage\s+-PathType\s+Leaf') {
	throw 'PackagePath must continue accepting both extracted directories and release ZIP files.'
}
if ($installText -notmatch 'ZipFile\]::ExtractToDirectory') {
	throw 'Release ZIP installation must retain the native extraction path.'
}

$fixtureRoot = Join-Path $PSScriptRoot 'bin\packaged-layout-contract'
if (Test-Path -LiteralPath $fixtureRoot) { Remove-Item -LiteralPath $fixtureRoot -Recurse -Force }
try {
	New-Item -ItemType Directory -Path (Join-Path $fixtureRoot 'bin') -Force | Out-Null
	Set-Content -LiteralPath (Join-Path $fixtureRoot 'dnSpy.exe') -Value ''
	Set-Content -LiteralPath (Join-Path $fixtureRoot 'dnSpy.Console.exe') -Value ''
	Set-Content -LiteralPath (Join-Path $fixtureRoot 'bin\dnSpy.Contracts.DnSpy.dll') -Value ''
	& $layoutTestPath -HostRoot $fixtureRoot -TargetFramework net10.0-windows

	Set-Content -LiteralPath (Join-Path $fixtureRoot 'dnSpy.Contracts.DnSpy.dll') -Value ''
	try {
		& $layoutTestPath -HostRoot $fixtureRoot -TargetFramework net10.0-windows
		throw 'Layout validator accepted a root dnSpy.Contracts.DnSpy.dll.'
	}
	catch {
		if ($_.Exception.Message -notmatch 'AppDirectories\.BinDirectory') { throw }
	}
	Remove-Item -LiteralPath (Join-Path $fixtureRoot 'dnSpy.Contracts.DnSpy.dll') -Force

	# Preserve the exact root listing captured from the T07-poisoned net48 package. This catches
	# both the BinDirectory-defining contract copy and its broader transitive dependency closure.
	Remove-Item -LiteralPath $fixtureRoot -Recurse -Force
	New-Item -ItemType Directory -Path (Join-Path $fixtureRoot 'bin') -Force | Out-Null
	Set-Content -LiteralPath (Join-Path $fixtureRoot 'bin\dnSpy.Contracts.DnSpy.dll') -Value ''
	foreach ($name in Get-Content -LiteralPath $poisonedRootFixture) {
		Set-Content -LiteralPath (Join-Path $fixtureRoot $name) -Value ''
	}
	try {
		& $layoutTestPath -HostRoot $fixtureRoot -TargetFramework net48
		throw 'Layout validator accepted the captured poisoned net48 root.'
	}
	catch {
		if ($_.Exception.Message -notmatch 'AppDirectories\.BinDirectory') { throw }
	}

	Remove-Item -LiteralPath $fixtureRoot -Recurse -Force
	New-Item -ItemType Directory -Path (Join-Path $fixtureRoot 'bin') -Force | Out-Null
	Set-Content -LiteralPath (Join-Path $fixtureRoot 'dnSpy.exe') -Value ''
	Set-Content -LiteralPath (Join-Path $fixtureRoot 'dnSpy.Console.exe') -Value ''
	Set-Content -LiteralPath (Join-Path $fixtureRoot 'bin\dnSpy.Contracts.DnSpy.dll') -Value ''

	Set-Content -LiteralPath (Join-Path $fixtureRoot 'stray.dll') -Value ''
	try {
		& $layoutTestPath -HostRoot $fixtureRoot -TargetFramework net10.0-windows
		throw 'Layout validator accepted an unexpected root file.'
	}
	catch {
		if ($_.Exception.Message -notmatch 'unexpected files') { throw }
	}
}
finally {
	if (Test-Path -LiteralPath $fixtureRoot) { Remove-Item -LiteralPath $fixtureRoot -Recurse -Force }
}

Write-Host 'PASSED  release ZIP, local directory, and packaged host layout contracts'
