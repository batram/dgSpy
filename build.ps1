param(
	[string]$buildtfm = 'all',
	[switch]$NoMsbuild,
	[string]$MSBuildPath = 'msbuild'
)
$ErrorActionPreference = 'Stop'

$netframework_tfm = 'net48'
$net_tfm = 'net5.0-windows'
$configuration = 'Release'
$net_baseoutput = "dnSpy\dnSpy\bin\$configuration"
$apphostpatcher_dir = "Build\AppHostPatcher"
$msbuildExe = $null

# Resolve the build tool before cleaning any generated output. A plain PowerShell session may not
# have MSBuild on PATH even though Visual Studio is installed; callers can pass -MSBuildPath.
if (-not $NoMsbuild) {
	$msbuildCommand = Get-Command -Name $MSBuildPath -CommandType Application -ErrorAction Stop
	$msbuildExe = $msbuildCommand.Source
}

#
# The reason we don't use dotnet build is that dotnet build doesn't support COM references yet https://github.com/dnSpy/dnSpy/issues/1053
#

function Build-NetFramework {
	Write-Host 'Building .NET Framework x86 and x64 binaries'

	$outdir = "$net_baseoutput\$netframework_tfm"
	# This directory is repackaged below so dnSpy executables stay at the root and their dependencies
	# move under bin. Reusing an already-packaged directory nests the previous bin again on every run
	# (net48\bin\bin, then bin\bin\bin). Start from a validated clean framework output instead.
	$expectedOutdir = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "$net_baseoutput\$netframework_tfm"))
	$resolvedOutdir = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot $outdir))
	if (-not $resolvedOutdir.Equals($expectedOutdir, [StringComparison]::OrdinalIgnoreCase)) {
		throw "Refusing to clean unexpected .NET Framework output: $resolvedOutdir"
	}
	if (Test-Path -LiteralPath $resolvedOutdir) {
		Remove-Item -LiteralPath $resolvedOutdir -Recurse -Force
	}

	if ($NoMsbuild) {
		dotnet build -v:m -c $configuration -f $netframework_tfm
		if ($LASTEXITCODE) { exit $LASTEXITCODE }
	}
	else {
		& $msbuildExe -v:m -m -restore -t:Build -p:Configuration=$configuration -p:TargetFramework=$netframework_tfm
		if ($LASTEXITCODE) { exit $LASTEXITCODE }
	}

	# move all files to a bin sub dir but keep the exe files
	Rename-Item -LiteralPath $outdir -NewName bin
	New-Item -ItemType Directory $outdir > $null
	Move-Item -LiteralPath $net_baseoutput\bin -Destination $outdir
	foreach ($filename in 'dnSpy-x86.exe', 'dnSpy-x86.exe.config', 'dnSpy-x86.pdb',
			 'dnSpy.exe', 'dnSpy.exe.config', 'dnSpy.pdb',
			 'dnSpy.Console.exe', 'dnSpy.Console.exe.config', 'dnSpy.Console.pdb') {
		Move-Item -LiteralPath $outdir\bin\$filename -Destination $outdir
	}
}

function Build-Net {
	param([string]$arch)

	Write-Host "Building .NET $arch binaries"

	$rid = "win-$arch"
	$outdir = "$net_baseoutput\$net_tfm\$rid"
	$publishDir = "$outdir\publish"

	if ($NoMsbuild) {
		dotnet publish -v:m -c $configuration -f $net_tfm -r $rid --self-contained
		if ($LASTEXITCODE) { exit $LASTEXITCODE }
	}
	else {
		& $msbuildExe -v:m -m -restore -t:Publish -p:Configuration=$configuration -p:TargetFramework=$net_tfm -p:RuntimeIdentifier=$rid -p:SelfContained=True
		if ($LASTEXITCODE) { exit $LASTEXITCODE }
	}

	# move all files to a bin sub dir but keep the exe apphosts
	$tmpbin = 'tmpbin'
	Rename-Item $publishDir $tmpbin
	New-Item -ItemType Directory $publishDir > $null
	Move-Item $outdir\$tmpbin $publishDir
	Rename-Item $publishDir\$tmpbin bin
	foreach ($exe in 'dnSpy.exe', 'dnSpy.Console.exe') {
		Move-Item $publishDir\bin\$exe $publishDir
		& $apphostpatcher_dir\bin\$configuration\$netframework_tfm\AppHostPatcher.exe $publishDir\$exe -d bin
		if ($LASTEXITCODE) { exit $LASTEXITCODE }
	}
}

$buildNet	 = $buildtfm -eq 'all' -or $buildtfm -eq 'netframework'
$buildNetX86 = $buildtfm -eq 'all' -or $buildtfm -eq 'net-x86'
$buildNetX64 = $buildtfm -eq 'all' -or $buildtfm -eq 'net-x64'

if ($buildNetX86 -or $buildNetX64) {
	if ($NoMsbuild) {
		dotnet build -v:m -c $configuration -f $netframework_tfm $apphostpatcher_dir\AppHostPatcher.csproj
		if ($LASTEXITCODE) { exit $LASTEXITCODE }
	}
	else {
		& $msbuildExe -v:m -m -restore -t:Build -p:Configuration=$configuration -p:TargetFramework=$netframework_tfm $apphostpatcher_dir\AppHostPatcher.csproj
		if ($LASTEXITCODE) { exit $LASTEXITCODE }
	}
}

if ($buildNet) {
	Build-NetFramework
}

if ($buildNetX86) {
	Build-Net x86
}

if ($buildNetX64) {
	Build-Net x64
}
