# Builds the dgSpy projects and deploys the extension into dnSpy.
# See docs/DGSPY_BASELINE.md. Scope: x64.
#
# net10 is the default because it is what ships: pack-dgspy.ps1 packages the net10 host and that is
# what install-dgspy.ps1 deploys and what launch_local_host runs. Testing net48 by default meant the
# live smoke proved a build no user runs. net48 remains fully supported via -TargetFramework net48.
param(
	[string]$Configuration = 'Release',
	[ValidateSet('net10.0-windows','net48')][string]$TargetFramework = 'net10.0-windows',
	# Defaults to the tree matching -TargetFramework; PowerShell cannot express that in the param block.
	[string]$DnSpyDir,
	[switch]$NoDeploy
)

$ErrorActionPreference = 'Stop'
# See build.ps1: keep MSBuild from leaving reusable worker nodes holding bin/obj handles.
$env:MSBUILDDISABLENODEREUSE = '1'

# The net10 host is a self-contained publish, so its runtime tree is the publish directory rather
# than the build output directory. Both layouts put dnSpy.exe at the root and the runtime under bin\.
$hostBuildCommand = if ($TargetFramework -eq 'net48') { '.\build.ps1 netframework' } else { '.\build.ps1 net-x64 -NoMsbuild' }
if ([string]::IsNullOrWhiteSpace($DnSpyDir)) {
	$DnSpyDir = if ($TargetFramework -eq 'net48') {
		"$PSScriptRoot\dnSpy\dnSpy\bin\Release\net48"
	} else {
		"$PSScriptRoot\dnSpy\dnSpy\bin\Release\net10.0-windows\win-x64\publish"
	}
}

$extensionProject = Join-Path $PSScriptRoot 'Extensions\dgSpy.Extension\dgSpy.Extension.csproj'
$gatewayProject = Join-Path $PSScriptRoot 'dgSpy.Gateway\dgSpy.Gateway.csproj'
$cliProject = Join-Path $PSScriptRoot 'dgSpy.Cli\dgSpy.Cli.csproj'
$extensionContractsProject = Join-Path $PSScriptRoot 'dgSpy.ExtensionContracts\dgSpy.ExtensionContracts.csproj'
$hookLabContractsProject = Join-Path $PSScriptRoot 'HookLab\HookLab.Contracts\HookLab.Contracts.csproj'

dotnet build $extensionContractsProject -c $Configuration --nologo -v:minimal
if ($LASTEXITCODE) { throw "Extension contracts build failed with exit code $LASTEXITCODE" }

dotnet build $hookLabContractsProject -c $Configuration --nologo -v:minimal
if ($LASTEXITCODE) { throw "HookLab contracts build failed with exit code $LASTEXITCODE" }

dotnet build $extensionProject -c $Configuration -f $TargetFramework --nologo -v:minimal
if ($LASTEXITCODE) { throw "Extension build failed with exit code $LASTEXITCODE" }

dotnet build $gatewayProject -c $Configuration --nologo -v:minimal
if ($LASTEXITCODE) { throw "Gateway build failed with exit code $LASTEXITCODE" }

dotnet build $cliProject -c $Configuration --nologo -v:minimal
if ($LASTEXITCODE) { throw "CLI build failed with exit code $LASTEXITCODE" }

if ($NoDeploy) { return }

if (-not (Test-Path $DnSpyDir)) {
	throw "dnSpy directory not found: $DnSpyDir. Build dnSpy first ($hostBuildCommand) or pass -DnSpyDir."
}

$extensionOutput = Join-Path $PSScriptRoot "Extensions\dgSpy.Extension\bin\$Configuration\$TargetFramework"
$runtimeBin = Join-Path $DnSpyDir 'bin'
if (-not (Test-Path -LiteralPath (Join-Path $runtimeBin 'dnSpy.Contracts.DnSpy.dll'))) {
	throw "Packaged dnSpy runtime directory not found: $runtimeBin. Run $hostBuildCommand first."
}
$deployDir = Join-Path $runtimeBin 'Extensions\dgSpy'

# dnSpy holds the extension assemblies open, so an instance running *from the deploy target*
# blocks the copy. Other dnSpy instances are unaffected.
$resolvedDnSpyDir = (Resolve-Path $DnSpyDir).Path
$running = @(Get-Process -Name 'dnSpy' -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($resolvedDnSpyDir, [StringComparison]::OrdinalIgnoreCase) })
if ($running.Count -gt 0) {
	throw "dnSpy is running from $resolvedDnSpyDir (PID $($running.Id -join ', ')). Close it before deploying."
}

# AppDirectories.BinDirectory is the directory containing dnSpy.Contracts.DnSpy.dll, which both the
# packaged net48 layout and the self-contained net10 publish place under bin\. dnSpy scans that
# directory and its Extensions\* children. Remove stale direct copies there so the extension cannot
# be composed twice.
foreach ($stale in Get-ChildItem $runtimeBin -Filter 'dgSpy.*' -File -ErrorAction SilentlyContinue) {
	Remove-Item $stale.FullName -Force
	Write-Host "Removed stale $($stale.Name) from $runtimeBin"
}

# Versions before the idempotent net48 packaging fix deployed beside dnSpy.exe, which is not an
# extension search path in the packaged layout. Remove that obsolete tree during migration.
$obsoleteDeployDir = Join-Path $DnSpyDir 'Extensions\dgSpy'
if (Test-Path -LiteralPath $obsoleteDeployDir) {
	Remove-Item -LiteralPath $obsoleteDeployDir -Recurse -Force
	$obsoleteParent = Split-Path $obsoleteDeployDir
	if (@(Get-ChildItem -LiteralPath $obsoleteParent -Force -ErrorAction SilentlyContinue).Count -eq 0) {
		Remove-Item -LiteralPath $obsoleteParent -Force
	}
}

New-Item -ItemType Directory -Path $deployDir -Force | Out-Null
# Remove the obsolete Newtonsoft payload left by earlier dgSpy deployments.
$obsoleteNewtonsoft = Join-Path $deployDir 'Newtonsoft.Json.dll'
if (Test-Path -LiteralPath $obsoleteNewtonsoft) { Remove-Item -LiteralPath $obsoleteNewtonsoft -Force }
$deployFiles = @('dgSpy.Extension.x.dll', 'dgSpy.Extension.x.pdb', 'dgSpy.Protocol.dll', 'dgSpy.Protocol.pdb')
if ($TargetFramework -eq 'net48') {
	# dgSpy.Protocol targets netstandard2.0, so under net48 its System.Text.Json compatibility
	# assemblies must sit beside the extension in the LoadFrom context. Copy only these dependencies,
	# never dnSpy's own contracts. The net10 runtime supplies all of these, and pack-dgspy.ps1 ships
	# the same four files this list starts with.
	$deployFiles += @(
		'System.Text.Json.dll', 'System.Text.Encodings.Web.dll', 'System.Memory.dll', 'System.Buffers.dll',
		'System.Runtime.CompilerServices.Unsafe.dll', 'System.Threading.Tasks.Extensions.dll', 'Microsoft.Bcl.AsyncInterfaces.dll'
	)
}
foreach ($file in $deployFiles) {
	Copy-Item (Join-Path $extensionOutput $file) $deployDir -Force
}

Write-Host "Deployed dgSpy extension to $deployDir"
