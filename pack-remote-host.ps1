param(
	[ValidateSet('Release')][string]$Configuration = 'Release',
	[string]$OutputDirectory = "$PSScriptRoot\artifacts\remote-host",
	[switch]$SkipBuild,
	[string]$HostPublishDirectory = "$PSScriptRoot\dnSpy\dnSpy\bin\Release\net10.0-windows\win-x64\publish"
	,[Parameter(Mandatory=$true)][ValidatePattern('^[A-Za-z0-9._-]+$')][string]$HostId
	,[Parameter(Mandatory=$true)][string]$GatewayAddress
	,[ValidateRange(1,65535)][int]$GatewayPort = 7352
	,[string]$GatewayHostsFile = "$OutputDirectory\gateway-hosts.json"
	,[switch]$UseTls
	,[ValidateRange(1,65535)][int]$GatewayTlsPort = 7353
)

$ErrorActionPreference = 'Stop'
# See build.ps1: keep MSBuild from leaving reusable worker nodes holding bin/obj handles.
$env:MSBUILDDISABLENODEREUSE = '1'
. (Join-Path $PSScriptRoot 'packaging\HookLabPayload.ps1')
function New-RandomSecret {
	$bytes=[byte[]]::new(32); $generator=[Security.Cryptography.RandomNumberGenerator]::Create()
	try { $generator.GetBytes($bytes); [Convert]::ToBase64String($bytes) } finally { $generator.Dispose() }
}
$targetFramework = 'net10.0-windows'
$runtimeIdentifier = 'win-x64'
$bundleName = "dgSpy-remote-host-$HostId-win-x64"
$bundleDirectory = Join-Path $OutputDirectory $bundleName
$archivePath = [IO.Path]::GetFullPath((Join-Path $OutputDirectory "$bundleName.zip"))
$extensionProject = Join-Path $PSScriptRoot 'Extensions\dgSpy.Extension\dgSpy.Extension.csproj'
$bootstrapProject = Join-Path $PSScriptRoot 'HookLab\HookLab.Bootstrap\HookLab.Bootstrap.csproj'
# The bootstrap is net48 whatever the host targets: the payload is injected into a CLR v4 target, not
# loaded by the host. It ships as one file that embeds and verifies its own dependencies.
$bootstrapAssembly = Join-Path $PSScriptRoot "HookLab\HookLab.Bootstrap\bin\$Configuration\net48\HookLab.Bootstrap.dll"
$nativeBootstrap = Join-Path $PSScriptRoot "HookLab\HookLab.NativeBootstrap\bin\$Configuration\HookLab.NativeBootstrap.x64.dll"
$extensionOutput = Join-Path $PSScriptRoot "Extensions\dgSpy.Extension\bin\$Configuration\$targetFramework"

if (-not $SkipBuild) {
	& (Join-Path $PSScriptRoot 'build.ps1') net-x64 -NoMsbuild
	if ($LASTEXITCODE) { throw "Self-contained dnSpy publish failed with exit code $LASTEXITCODE." }
	dotnet build $extensionProject -c $Configuration -f $targetFramework --nologo -v:minimal
	if ($LASTEXITCODE) { throw "dgSpy extension build failed with exit code $LASTEXITCODE." }
	dotnet build $bootstrapProject -c $Configuration --nologo -v:minimal
	if ($LASTEXITCODE) { throw "HookLab bootstrap build failed with exit code $LASTEXITCODE." }
	& (Join-Path $PSScriptRoot 'tools\build-hooklab-native.ps1') -Configuration $Configuration | Write-Host
	if (-not $?) { throw 'HookLab native initializer build failed.' }
}

$requiredHostFiles = @('dnSpy.exe', 'bin\dnSpy.dll', 'bin\dnSpy.Contracts.DnSpy.dll', 'bin\hostfxr.dll', 'bin\hostpolicy.dll', 'bin\coreclr.dll', 'bin\clrjit.dll')
foreach ($relativePath in $requiredHostFiles) {
	if (-not (Test-Path -LiteralPath (Join-Path $HostPublishDirectory $relativePath) -PathType Leaf)) {
		throw "Self-contained host file is missing: $relativePath. Build with .\build.ps1 net-x64 -NoMsbuild."
	}
}
$extensionFiles = @('dgSpy.Extension.x.dll', 'dgSpy.Extension.x.pdb', 'dgSpy.Protocol.dll', 'dgSpy.Protocol.pdb', 'HookLab.Contracts.dll', 'HookLab.Contracts.pdb', 'HookLab.Host.Transport.dll', 'HookLab.Host.Transport.pdb')
foreach ($fileName in $extensionFiles) {
	if (-not (Test-Path -LiteralPath (Join-Path $extensionOutput $fileName) -PathType Leaf)) { throw "Extension output is missing: $fileName." }
}
if (-not (Test-Path -LiteralPath $bootstrapAssembly -PathType Leaf)) { throw "HookLab bootstrap output is missing: $bootstrapAssembly. Build without -SkipBuild." }
if (-not (Test-Path -LiteralPath $nativeBootstrap -PathType Leaf)) { throw "HookLab native initializer output is missing: $nativeBootstrap. Build without -SkipBuild." }

$resolvedOutput = [IO.Path]::GetFullPath($OutputDirectory)
$resolvedBundle = [IO.Path]::GetFullPath($bundleDirectory)
if (-not $resolvedBundle.StartsWith($resolvedOutput + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw "Refusing to replace unexpected bundle directory: $resolvedBundle" }
New-Item -ItemType Directory -Path $resolvedOutput -Force | Out-Null
if (Test-Path -LiteralPath $resolvedBundle) { Remove-Item -LiteralPath $resolvedBundle -Recurse -Force }
Copy-Item -LiteralPath $HostPublishDirectory -Destination $resolvedBundle -Recurse
$deployDirectory = Join-Path $resolvedBundle 'bin\Extensions\dgSpy'
New-Item -ItemType Directory -Path $deployDirectory -Force | Out-Null
foreach ($fileName in $extensionFiles) { Copy-Item -LiteralPath (Join-Path $extensionOutput $fileName) -Destination $deployDirectory }
$rootProtocol = Join-Path $resolvedBundle 'bin\dgSpy.Protocol.dll'
$extensionProtocol = Join-Path $deployDirectory 'dgSpy.Protocol.dll'
if (Test-Path -LiteralPath $rootProtocol -PathType Leaf) {
	if ((Get-FileHash -LiteralPath $rootProtocol -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $extensionProtocol -Algorithm SHA256).Hash) {
		throw 'The remote bundle has different app-base and extension dgSpy.Protocol.dll files; dnSpy would silently load the app-base copy.'
	}
}
$launcherDirectory = Join-Path $resolvedBundle 'launcher'
New-Item -ItemType Directory -Path $launcherDirectory -Force | Out-Null
foreach ($launcher in 'Start-dgSpyRemoteHost.ps1', 'Start-dgSpyRemoteHost.cmd') {
	Copy-Item -LiteralPath (Join-Path $PSScriptRoot "packaging\remote-host\$launcher") -Destination $launcherDirectory
}
# The HookLab payload, staged into the bundle root the same way pack-dgspy.ps1 stages it into cli\: one
# file under hooklab\, never beside the extension, with its digest recorded twice - once in the payload
# manifest and once in this bundle's own per-file manifest below.
$bootstrapSha = Write-HookLabPayload -BootstrapAssembly $bootstrapAssembly -NativeBootstrap $nativeBootstrap -HostRoot $resolvedBundle
Write-Host "HookLab payload $($bootstrapSha.Substring(0,12)) staged at hooklab\$script:HookLabPayloadFileName"
$stateDirectory = Join-Path $resolvedBundle 'state'
New-Item -ItemType Directory -Path $stateDirectory -Force | Out-Null
$credentialBytes = [byte[]]::new(32)
$credentialGenerator = [Security.Cryptography.RandomNumberGenerator]::Create()
try { $credentialGenerator.GetBytes($credentialBytes) }
finally { $credentialGenerator.Dispose() }
$credential = [Convert]::ToBase64String($credentialBytes)
[IO.File]::WriteAllText((Join-Path $stateDirectory 'host.id'),$HostId,[Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText((Join-Path $stateDirectory 'rpc.token'),$credential,[Text.UTF8Encoding]::new($false))
$transport = if ($UseTls) { 'tls' } else { 'plaintext' }
$remoteConfiguration = [ordered]@{ format_version=1; host_id=$HostId; gateway_address=$GatewayAddress; gateway_port=if($UseTls){$GatewayTlsPort}else{$GatewayPort}; transport=$transport }
[IO.File]::WriteAllText((Join-Path $resolvedBundle 'remote-host.json'),(($remoteConfiguration | ConvertTo-Json) + "`n"),[Text.UTF8Encoding]::new($false))
$disabledPolicy = [ordered]@{ format_version=1; defaults=[ordered]@{ runtime_hooks=$false; custom_hook_code=$false; hook_export=$false }; entries=@() }
[IO.File]::WriteAllText((Join-Path $resolvedBundle 'capability-policy.json'),(($disabledPolicy | ConvertTo-Json -Depth 4) + "`n"),[Text.UTF8Encoding]::new($false))
$gatewayDirectory = Split-Path -Parent ([IO.Path]::GetFullPath($GatewayHostsFile))
New-Item -ItemType Directory -Path $gatewayDirectory -Force | Out-Null
$centralTokenFile = Join-Path $gatewayDirectory "$HostId.token"
[IO.File]::WriteAllText($centralTokenFile,$credential,[Text.UTF8Encoding]::new($false))
$gatewayDocument = if (Test-Path -LiteralPath $GatewayHostsFile) { Get-Content -LiteralPath $GatewayHostsFile -Raw | ConvertFrom-Json } else { [pscustomobject]@{ hosts=@() } }
if (@($gatewayDocument.hosts | Where-Object host_id -eq $HostId).Count) { throw "Gateway configuration already contains host_id '$HostId'." }
$gatewayHost = [ordered]@{ host_id=$HostId; display_name=$HostId; transport=if($UseTls){'outbound_tls'}else{'outbound'}; token_file=(Split-Path -Leaf $centralTokenFile) }
if ($UseTls) {
	$certificateTool=Join-Path $PSScriptRoot 'Build\RemoteCertificateTool\RemoteCertificateTool.csproj'
	$gatewayPfx=Join-Path $gatewayDirectory 'gateway-server.pfx'; $gatewayCer=Join-Path $gatewayDirectory 'gateway-server.cer'; $gatewayPasswordFile=Join-Path $gatewayDirectory 'gateway-server.password'
	if (-not (Test-Path -LiteralPath $gatewayPfx -PathType Leaf) -or -not (Test-Path -LiteralPath $gatewayCer -PathType Leaf)) {
		$gatewayPassword=New-RandomSecret; [IO.File]::WriteAllText($gatewayPasswordFile,$gatewayPassword,[Text.UTF8Encoding]::new($false))
		& dotnet run --project $certificateTool --configuration Release -- server $GatewayAddress $gatewayPfx $gatewayCer $gatewayPasswordFile
		if ($LASTEXITCODE) { throw "Gateway certificate generation failed: $LASTEXITCODE" }
	}
	if (-not (Test-Path -LiteralPath $gatewayPasswordFile -PathType Leaf)) { throw 'The existing Gateway certificate has no password file; remove the Gateway certificate files and package again.' }
	$certificateDirectory=Join-Path $resolvedBundle 'certificates'; New-Item -ItemType Directory -Path $certificateDirectory -Force | Out-Null
	$clientPfx=Join-Path $certificateDirectory 'client.pfx'; $clientPasswordFile=Join-Path $certificateDirectory 'client.password'; $centralClientCer=Join-Path $gatewayDirectory "$HostId-client.cer"
	$clientPassword=New-RandomSecret; [IO.File]::WriteAllText($clientPasswordFile,$clientPassword,[Text.UTF8Encoding]::new($false))
	& dotnet run --project $certificateTool --configuration Release -- client $HostId $clientPfx $centralClientCer $clientPasswordFile
	if ($LASTEXITCODE) { throw "Client certificate generation failed: $LASTEXITCODE" }
	Copy-Item -LiteralPath $gatewayCer -Destination (Join-Path $certificateDirectory 'gateway-server.cer')
	$remoteConfiguration.client_certificate_file='certificates/client.pfx'; $remoteConfiguration.client_certificate_password_file='certificates/client.password'; $remoteConfiguration.gateway_certificate_file='certificates/gateway-server.cer'
	$gatewayHost.client_certificate_file=(Split-Path -Leaf $centralClientCer)
	$gatewayDocument | Add-Member -NotePropertyName tls -NotePropertyValue ([pscustomobject][ordered]@{ server_certificate_file=(Split-Path -Leaf $gatewayPfx); server_certificate_password_file=(Split-Path -Leaf $gatewayPasswordFile); port=$GatewayTlsPort }) -Force
}
[IO.File]::WriteAllText((Join-Path $resolvedBundle 'remote-host.json'),(([pscustomobject]$remoteConfiguration | ConvertTo-Json) + "`n"),[Text.UTF8Encoding]::new($false))
$gatewayDocument.hosts = @($gatewayDocument.hosts) + [pscustomobject]$gatewayHost
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

# Re-verified from the staged bytes after everything else has been written, so a later step that
# disturbed the payload fails the package instead of shipping.
$verifiedSha = Test-HookLabPayload -HostRoot $resolvedBundle -ExpectedSha256 $bootstrapSha
Write-Host "Verified bundled HookLab payload $($verifiedSha.Substring(0,12))"

if (Test-Path -LiteralPath $archivePath) { Remove-Item -LiteralPath $archivePath -Force }
Push-Location $resolvedBundle
try {
	& tar.exe -a -c -f $archivePath -- *
	if ($LASTEXITCODE) { throw "Archive creation failed with exit code $LASTEXITCODE." }
}
finally { Pop-Location }
Write-Host "Remote host bundle: $resolvedBundle"
Write-Host "Archive: $archivePath"
