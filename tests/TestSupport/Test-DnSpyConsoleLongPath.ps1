param(
	[Parameter(Mandatory = $true)]
	[string]$LayoutRoot
)

$ErrorActionPreference = 'Stop'
$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new()
$layout = [IO.Path]::GetFullPath($LayoutRoot)
$console = Join-Path $layout 'bin\dnSpy.Console.dll'
$source = Join-Path $layout 'bin\dnSpy.Contracts.Logic.dll'
if (-not (Test-Path -LiteralPath $console -PathType Leaf)) { throw "Missing packaged console: $console" }
if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Missing managed fixture: $source" }

$tempBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$testRoot = Join-Path $tempBase ("dgspy-console-long-path-" + [Guid]::NewGuid().ToString('N'))
$deepDirectory = $testRoot
while ($deepDirectory.Length -lt 280) {
	$deepDirectory = Join-Path $deepDirectory '0123456789abcdef0123456789abcdef'
}
$deepFile = Join-Path $deepDirectory 'dnSpy.Contracts.Logic.dll'

try {
	[IO.Directory]::CreateDirectory($deepDirectory) | Out-Null
	[IO.File]::Copy($source, $deepFile)
	$output = (& dotnet $console --no-color --md 0x02000002 $deepFile 2>&1 | Out-String)
	if ($LASTEXITCODE -ne 0) { throw "dnSpy.Console rejected a managed assembly at a long path:`n$output" }
	if ($output -notmatch 'Token:\s+0x02000002') { throw "dnSpy.Console returned no decompiled type for the long-path fixture:`n$output" }
	Write-Host "PASS  dnSpy.Console long-path managed assembly load ($($deepFile.Length) characters)"
}
finally {
	$resolvedRoot = [IO.Path]::GetFullPath($testRoot)
	if (-not $resolvedRoot.StartsWith($tempBase, [StringComparison]::OrdinalIgnoreCase)) {
		throw "Refusing to remove unexpected test path: $resolvedRoot"
	}
	if ([IO.Directory]::Exists($resolvedRoot)) { [IO.Directory]::Delete($resolvedRoot, $true) }
}
