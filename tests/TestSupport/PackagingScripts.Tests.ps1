$ErrorActionPreference = 'Stop'
$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new()

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$packPath = Join-Path $repoRoot 'pack-dgspy.ps1'
$installPath = Join-Path $repoRoot 'install-dgspy.ps1'

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

Write-Host 'PASSED  release ZIP and local directory packaging contracts'
