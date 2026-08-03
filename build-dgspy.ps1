# Builds the dgSpy projects and deploys the extension into dnSpy.
# See docs/DGSPY_BASELINE.md. Scope: x64, net48 dnSpy.
param(
	[string]$Configuration = 'Release',
	[string]$DnSpyDir = "$PSScriptRoot\dnSpy\dnSpy\bin\Release\net48",
	[switch]$NoDeploy
)

$ErrorActionPreference = 'Stop'

$extensionProject = Join-Path $PSScriptRoot 'Extensions\dgSpy.Extension\dgSpy.Extension.csproj'
$gatewayProject = Join-Path $PSScriptRoot 'dgSpy.Gateway\dgSpy.Gateway.csproj'

dotnet build $extensionProject -c $Configuration -f net48 --nologo -v:minimal
if ($LASTEXITCODE) { throw "Extension build failed with exit code $LASTEXITCODE" }

dotnet build $gatewayProject -c $Configuration --nologo -v:minimal
if ($LASTEXITCODE) { throw "Gateway build failed with exit code $LASTEXITCODE" }

if ($NoDeploy) { return }

if (-not (Test-Path $DnSpyDir)) {
	throw "dnSpy directory not found: $DnSpyDir. Build dnSpy first (.\build.ps1 netframework) or pass -DnSpyDir."
}

$extensionOutput = Join-Path $PSScriptRoot "Extensions\dgSpy.Extension\bin\$Configuration\net48"
$deployDir = Join-Path $DnSpyDir 'Extensions\dgSpy'

# dnSpy holds the extension assemblies open, so an instance running *from the deploy target*
# blocks the copy. Other dnSpy instances are unaffected.
$resolvedDnSpyDir = (Resolve-Path $DnSpyDir).Path
$running = @(Get-Process -Name 'dnSpy' -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($resolvedDnSpyDir, [StringComparison]::OrdinalIgnoreCase) })
if ($running.Count -gt 0) {
	throw "dnSpy is running from $resolvedDnSpyDir (PID $($running.Id -join ', ')). Close it before deploying."
}

# dnSpy scans its bin directory *and* Extensions\* for *.x.dll. A stale copy left directly in the
# bin directory would be composed a second time, so remove any before deploying.
foreach ($stale in Get-ChildItem $DnSpyDir -Filter 'dgSpy.*' -File -ErrorAction SilentlyContinue) {
	Remove-Item $stale.FullName -Force
	Write-Host "Removed stale $($stale.Name) from $DnSpyDir"
}

New-Item -ItemType Directory -Path $deployDir -Force | Out-Null
# Newtonsoft.Json is a dependency of dgSpy.Protocol and is not shipped by dnSpy. The LoadFrom
# context probes the extension's own directory, so it must sit next to the extension.
foreach ($file in 'dgSpy.Extension.x.dll', 'dgSpy.Extension.x.pdb', 'dgSpy.Protocol.dll', 'dgSpy.Protocol.pdb', 'Newtonsoft.Json.dll') {
	Copy-Item (Join-Path $extensionOutput $file) $deployDir -Force
}

Write-Host "Deployed dgSpy extension to $deployDir"
