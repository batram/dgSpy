param(
	[Parameter(Mandatory=$true)][string]$HostRoot,
	[Parameter(Mandatory=$true)][ValidateSet('net48','net10.0-windows')][string]$TargetFramework
)

$ErrorActionPreference = 'Stop'
$resolvedRoot = [IO.Path]::GetFullPath($HostRoot)
if (-not (Test-Path -LiteralPath $resolvedRoot -PathType Container)) {
	throw "Packaged host root not found: $resolvedRoot"
}

$nestedContract = Join-Path $resolvedRoot 'bin\dnSpy.Contracts.DnSpy.dll'
if (-not (Test-Path -LiteralPath $nestedContract -PathType Leaf)) {
	throw "Packaged host is missing bin\dnSpy.Contracts.DnSpy.dll: $resolvedRoot"
}
$rootContract = Join-Path $resolvedRoot 'dnSpy.Contracts.DnSpy.dll'
if (Test-Path -LiteralPath $rootContract -PathType Leaf) {
	throw "Packaged host root contains dnSpy.Contracts.DnSpy.dll. App-base probing would move AppDirectories.BinDirectory out of bin and hide themes and extensions: $rootContract"
}

$allowed = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$names = if ($TargetFramework -eq 'net48') {
	@('dnSpy-x86.exe','dnSpy-x86.exe.config','dnSpy-x86.pdb','dnSpy.exe','dnSpy.exe.config','dnSpy.pdb','dnSpy.Console.exe','dnSpy.Console.exe.config','dnSpy.Console.pdb')
}
else {
	@('dnSpy.exe','dnSpy.Console.exe')
}
foreach ($name in $names) { $null = $allowed.Add($name) }
$unexpected = @(Get-ChildItem -LiteralPath $resolvedRoot -File | Where-Object { -not $allowed.Contains($_.Name) })
if ($unexpected.Count -ne 0) {
	throw "Packaged host root contains unexpected files that can override bin dependencies: $($unexpected.Name -join ', ')"
}
