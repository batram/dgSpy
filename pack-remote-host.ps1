param(
	[string]$Configuration = 'Release',
	[string]$OutputDirectory = "$PSScriptRoot\artifacts\remote-host",
	[switch]$SkipBuild,
	[string]$HostPublishDirectory = "$PSScriptRoot\dnSpy\dnSpy\bin\Release\net10.0-windows\win-x64\publish"
	,[Parameter(Mandatory=$true)][ValidatePattern('^[A-Za-z0-9._-]+$')][string]$HostId
	,[Parameter(Mandatory=$true)][string]$GatewayAddress
	,[ValidateRange(1,65535)][int]$GatewayPort = 7352
	,[string]$GatewayHostsFile = "$OutputDirectory\gateway-hosts.json"
)

$ErrorActionPreference = 'Stop'
$targetFramework = 'net10.0-windows'
$runtimeIdentifier = 'win-x64'
$bundleName = "dgSpy-remote-host-$HostId-win-x64"
$bundleDirectory = Join-Path $OutputDirectory $bundleName
$archivePath = [IO.Path]::GetFullPath((Join-Path $OutputDirectory "$bundleName.zip"))
$extensionProject = Join-Path $PSScriptRoot 'Extensions\dgSpy.Extension\dgSpy.Extension.csproj'
$extensionOutput = Join-Path $PSScriptRoot "Extensions\dgSpy.Extension\bin\$Configuration\$targetFramework"

if (-not $SkipBuild) {
	& (Join-Path $PSScriptRoot 'build.ps1') net-x64 -NoMsbuild
	if ($LASTEXITCODE) { throw "Self-contained dnSpy publish failed with exit code $LASTEXITCODE." }
	dotnet build $extensionProject -c $Configuration -f $targetFramework --nologo -v:minimal
	if ($LASTEXITCODE) { throw "dgSpy extension build failed with exit code $LASTEXITCODE." }
}

$requiredHostFiles = @('dnSpy.exe', 'bin\dnSpy.dll', 'bin\dnSpy.Contracts.DnSpy.dll', 'bin\hostfxr.dll', 'bin\hostpolicy.dll', 'bin\coreclr.dll', 'bin\clrjit.dll')
foreach ($relativePath in $requiredHostFiles) {
	if (-not (Test-Path -LiteralPath (Join-Path $HostPublishDirectory $relativePath) -PathType Leaf)) {
		throw "Self-contained host file is missing: $relativePath. Build with .\build.ps1 net-x64 -NoMsbuild."
	}
}
$extensionFiles = @('dgSpy.Extension.x.dll', 'dgSpy.Extension.x.pdb', 'dgSpy.Protocol.dll', 'dgSpy.Protocol.pdb', 'Newtonsoft.Json.dll')
foreach ($fileName in $extensionFiles) {
	if (-not (Test-Path -LiteralPath (Join-Path $extensionOutput $fileName) -PathType Leaf)) { throw "Extension output is missing: $fileName." }
}

$resolvedOutput = [IO.Path]::GetFullPath($OutputDirectory)
$resolvedBundle = [IO.Path]::GetFullPath($bundleDirectory)
if (-not $resolvedBundle.StartsWith($resolvedOutput + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw "Refusing to replace unexpected bundle directory: $resolvedBundle" }
New-Item -ItemType Directory -Path $resolvedOutput -Force | Out-Null
if (Test-Path -LiteralPath $resolvedBundle) { Remove-Item -LiteralPath $resolvedBundle -Recurse -Force }
Copy-Item -LiteralPath $HostPublishDirectory -Destination $resolvedBundle -Recurse
$deployDirectory = Join-Path $resolvedBundle 'bin\Extensions\dgSpy'
New-Item -ItemType Directory -Path $deployDirectory -Force | Out-Null
foreach ($fileName in $extensionFiles) { Copy-Item -LiteralPath (Join-Path $extensionOutput $fileName) -Destination $deployDirectory }
$launcherDirectory = Join-Path $resolvedBundle 'launcher'
New-Item -ItemType Directory -Path $launcherDirectory -Force | Out-Null
foreach ($launcher in 'Start-dgSpyRemoteHost.ps1', 'Start-dgSpyRemoteHost.cmd') {
	Copy-Item -LiteralPath (Join-Path $PSScriptRoot "packaging\remote-host\$launcher") -Destination $launcherDirectory
}
$stateDirectory = Join-Path $resolvedBundle 'state'
New-Item -ItemType Directory -Path $stateDirectory -Force | Out-Null
$credentialBytes = [byte[]]::new(32)
$credentialGenerator = [Security.Cryptography.RandomNumberGenerator]::Create()
try { $credentialGenerator.GetBytes($credentialBytes) }
finally { $credentialGenerator.Dispose() }
$credential = [Convert]::ToBase64String($credentialBytes)
[IO.File]::WriteAllText((Join-Path $stateDirectory 'host.id'),$HostId,[Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText((Join-Path $stateDirectory 'rpc.token'),$credential,[Text.UTF8Encoding]::new($false))
$remoteConfiguration = [pscustomobject][ordered]@{ format_version=1; host_id=$HostId; gateway_address=$GatewayAddress; gateway_port=$GatewayPort }
[IO.File]::WriteAllText((Join-Path $resolvedBundle 'remote-host.json'),(($remoteConfiguration | ConvertTo-Json) + "`n"),[Text.UTF8Encoding]::new($false))
$gatewayDirectory = Split-Path -Parent ([IO.Path]::GetFullPath($GatewayHostsFile))
New-Item -ItemType Directory -Path $gatewayDirectory -Force | Out-Null
$centralTokenFile = Join-Path $gatewayDirectory "$HostId.token"
[IO.File]::WriteAllText($centralTokenFile,$credential,[Text.UTF8Encoding]::new($false))
$gatewayDocument = if (Test-Path -LiteralPath $GatewayHostsFile) { Get-Content -LiteralPath $GatewayHostsFile -Raw | ConvertFrom-Json } else { [pscustomobject]@{ hosts=@() } }
if (@($gatewayDocument.hosts | Where-Object host_id -eq $HostId).Count) { throw "Gateway configuration already contains host_id '$HostId'." }
$gatewayDocument.hosts = @($gatewayDocument.hosts) + [pscustomobject][ordered]@{ host_id=$HostId; display_name=$HostId; transport='outbound'; token_file=(Split-Path -Leaf $centralTokenFile) }
[IO.File]::WriteAllText([IO.Path]::GetFullPath($GatewayHostsFile),(($gatewayDocument | ConvertTo-Json -Depth 4) + "`n"),[Text.UTF8Encoding]::new($false))

# Mutable state and the manifest itself are excluded. Fixed ordering makes identical staged bytes
# produce identical manifest bytes.
$manifestFiles = Get-ChildItem -LiteralPath $resolvedBundle -File -Recurse |
	Where-Object { $_.FullName -notlike "$(Join-Path $resolvedBundle 'state')\*" -and $_.Name -ne 'manifest.json' } |
	ForEach-Object {
		[pscustomobject][ordered]@{
			path = $_.FullName.Substring($resolvedBundle.Length + 1).Replace('\', '/')
			size = $_.Length
			sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
		}
	} | Sort-Object path
$manifest = [pscustomobject][ordered]@{ format_version = 1; bundle = $bundleName; target_framework = $targetFramework; runtime_identifier = $runtimeIdentifier; self_contained = $true; files = @($manifestFiles) }
$manifestJson = $manifest | ConvertTo-Json -Depth 4
[IO.File]::WriteAllText((Join-Path $resolvedBundle 'manifest.json'), $manifestJson + "`n", [Text.UTF8Encoding]::new($false))

if (Test-Path -LiteralPath $archivePath) { Remove-Item -LiteralPath $archivePath -Force }
Push-Location $resolvedBundle
try {
	& tar.exe -a -c -f $archivePath -- *
	if ($LASTEXITCODE) { throw "Archive creation failed with exit code $LASTEXITCODE." }
}
finally { Pop-Location }
Write-Host "Remote host bundle: $resolvedBundle"
Write-Host "Archive: $archivePath"
