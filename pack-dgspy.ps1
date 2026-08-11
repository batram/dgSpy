param(
  [ValidateSet('Release')][string]$Configuration='Release',
  [ValidateSet('win-x64')][string]$Runtime='win-x64',
  [string]$OutputDirectory="$PSScriptRoot\artifacts\dgspy",
  [ValidateSet('Optimal','Fastest','NoCompression')][string]$CompressionLevel='Optimal',
  [switch]$DirectoryPackage
)
$ErrorActionPreference='Stop'
# See build.ps1: keep MSBuild from leaving reusable worker nodes holding bin/obj handles.
$env:MSBUILDDISABLENODEREUSE='1'
[void][Reflection.Assembly]::LoadWithPartialName('System.IO.Compression.FileSystem')
. (Join-Path $PSScriptRoot 'packaging\HookLabPayload.ps1')
$packStopwatch=[Diagnostics.Stopwatch]::StartNew()
$lastPhase=$packStopwatch.Elapsed
$hostFrameworkOverrides=@(
  'Microsoft.VisualBasic.dll',
  'System.Diagnostics.EventLog.dll',
  'System.Drawing.dll',
  'System.Security.Cryptography.Pkcs.dll',
  'System.Security.Cryptography.Xml.dll',
  'WindowsBase.dll'
)

function Merge-PublishTree([string]$Source,[string]$Destination) {
  $sourceRoot=[IO.Path]::GetFullPath($Source).TrimEnd([IO.Path]::DirectorySeparatorChar)+[IO.Path]::DirectorySeparatorChar
  foreach($sourceFile in Get-ChildItem -LiteralPath $Source -File -Recurse) {
    $sourcePath=[IO.Path]::GetFullPath($sourceFile.FullName)
    if(-not $sourcePath.StartsWith($sourceRoot,[StringComparison]::OrdinalIgnoreCase)) { throw "Publish input escaped its root: $sourcePath" }
    $relative=$sourcePath.Substring($sourceRoot.Length)
    $destinationFile=Join-Path $Destination $relative
    $destinationDirectory=Split-Path -Parent $destinationFile
    New-Item -ItemType Directory -Path $destinationDirectory -Force|Out-Null
    if(Test-Path -LiteralPath $destinationFile -PathType Leaf) {
      $sourceHash=(Get-FileHash -LiteralPath $sourceFile.FullName -Algorithm SHA256).Hash
      $destinationHash=(Get-FileHash -LiteralPath $destinationFile -Algorithm SHA256).Hash
      if($sourceHash -ne $destinationHash) {
        $sourceVersion=(Get-Item -LiteralPath $sourceFile.FullName).VersionInfo.FileVersion
        $destinationVersion=(Get-Item -LiteralPath $destinationFile).VersionInfo.FileVersion
        if($relative -notin $hostFrameworkOverrides -or $sourceVersion -ne $destinationVersion) {
          throw "Publish collision has incompatible content: $relative"
        }
        # WindowsDesktop carries framework-specific facades/implementations for these assemblies.
        # Preserve dnSpy's copy when the .NET servicing file version is identical.
      }
      continue
    }
    Copy-Item -LiteralPath $sourceFile.FullName -Destination $destinationFile
  }
}

# Deliberately not a second tree-hash implementation. DeploymentService.HashTree is the one authority for
# payload identity; reimplementing it here in PowerShell - which has no Path.GetRelativePath on .NET
# Framework and sorts with culture rules where the gateway sorts ordinally - would drift for exactly the
# file names nobody tests. The package instead records signals that cannot be expressed two ways: an exact
# hash of the one assembly whose staleness is otherwise invisible, plus a coarse shape of the tree.
function Get-PayloadShape([string]$Root) {
  $files=@(Get-ChildItem -LiteralPath $Root -File -Recurse)
  return [ordered]@{file_count=$files.Count;payload_bytes=[int64](($files|Measure-Object -Property Length -Sum).Sum)}
}
# Provenance is best-effort: a release built from an exported tree has no git, and that must not fail the
# pack. An absent commit is reported as absent, never as a plausible-looking placeholder.
function Get-GitValue([string[]]$GitArguments) {
  try {
    $value=(& git -C $PSScriptRoot @GitArguments 2>$null)
    if($LASTEXITCODE -ne 0) { return $null }
    return ($value|Out-String).Trim()
  }
  catch { return $null }
}

function Write-PackTiming([string]$Phase) {
  $now=$packStopwatch.Elapsed
  Write-Host ("dgSpy package timing: {0} {1:n1}s (total {2:n1}s)" -f $Phase,($now-$script:lastPhase).TotalSeconds,$now.TotalSeconds)
  $script:lastPhase=$now
}

$resolved=[IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $resolved -Force|Out-Null
$staging=Join-Path $resolved ('.staging-'+[Guid]::NewGuid().ToString('N'))
$cli=Join-Path $staging 'cli'
$cliPublish=Join-Path $staging '.cli-publish'
$gatewayPublish=Join-Path $staging '.gateway-publish'
try {
  # Release engineering owns compilation. The installed Gateway only personalizes this immutable
  # payload; it never discovers a source checkout or invokes an SDK.
  & (Join-Path $PSScriptRoot 'build.ps1') net-x64 -NoMsbuild
  if(-not $?){ throw 'Remote host publish failed.' }
  Write-PackTiming 'dnSpy build'
  # build.ps1 deliberately wipes extensions from the published host. Always follow it with the one
  # authoritative dgSpy deployer; a direct extension build creates bin output but leaves the runnable
  # repository host without its extension and HookLab payload. Packaging and local live tests must be
  # able to use the same complete publish tree after this command returns.
  & (Join-Path $PSScriptRoot 'build-dgspy.ps1') -Configuration $Configuration -TargetFramework net10.0-windows
  if(-not $?){ throw 'dgSpy extension and HookLab deployment failed.' }
  Write-PackTiming 'extension and HookLab deployment'
  dotnet publish (Join-Path $PSScriptRoot 'dgSpy.Cli\dgSpy.Cli.csproj') -c $Configuration -r $Runtime --self-contained true -o $cliPublish --nologo -v:minimal
  if($LASTEXITCODE){ throw "CLI publish failed: $LASTEXITCODE" }
  dotnet publish (Join-Path $PSScriptRoot 'dgSpy.Gateway\dgSpy.Gateway.csproj') -c $Configuration -r $Runtime --self-contained true -o $gatewayPublish --nologo -v:minimal
  if($LASTEXITCODE){ throw "Gateway publish failed: $LASTEXITCODE" }
  Write-PackTiming 'CLI and Gateway publish'
  $hostPublish=Join-Path $PSScriptRoot 'dnSpy\dnSpy\bin\Release\net10.0-windows\win-x64\publish'
  $extensionOutput=Join-Path $PSScriptRoot "Extensions\dgSpy.Extension\bin\$Configuration\net10.0-windows"
	$repositoryExtension=Join-Path $hostPublish 'bin\Extensions\dgSpy\dgSpy.Extension.x.dll'
	if(-not (Test-Path -LiteralPath $repositoryExtension -PathType Leaf)){ throw "Packaging left the repository host without dgSpy.Extension.x.dll: $repositoryExtension" }
	$null=Test-HookLabPayload -HostRoot $hostPublish
  foreach($required in 'dnSpy.exe','bin\dnSpy.dll','bin\hostfxr.dll','bin\coreclr.dll'){
    if(-not (Test-Path -LiteralPath (Join-Path $hostPublish $required) -PathType Leaf)){ throw "Remote payload build is incomplete: $required" }
  }
  New-Item -ItemType Directory -Path $cli -Force|Out-Null
  Get-ChildItem -LiteralPath $hostPublish -Force|Copy-Item -Destination $cli -Recurse
  $sharedBin=Join-Path $cli 'bin'
  Merge-PublishTree $cliPublish $sharedBin
  Merge-PublishTree $gatewayPublish $sharedBin
  Remove-Item -LiteralPath $cliPublish,$gatewayPublish -Recurse -Force
  $extensionDestination=Join-Path $sharedBin 'Extensions\dgSpy';New-Item -ItemType Directory -Path $extensionDestination -Force|Out-Null
  foreach($file in 'dgSpy.Extension.x.dll','dgSpy.Extension.x.pdb','dgSpy.Protocol.dll','dgSpy.Protocol.pdb','HookLab.Contracts.dll','HookLab.Contracts.pdb','HookLab.Host.Transport.dll','HookLab.Host.Transport.pdb'){
    Copy-Item -LiteralPath (Join-Path $extensionOutput $file) -Destination $extensionDestination
  }
  # Staged after the host tree and before the shape is measured, so file_count and payload_bytes cover it
  # and an interrupted copy is caught by the installer's coarse check as well as by its digest.
  $bootstrapAssembly=Join-Path $PSScriptRoot "HookLab\HookLab.Bootstrap\bin\$Configuration\net48\HookLab.Bootstrap.dll"
  $bootstrapSha=Write-HookLabPayload -BootstrapAssembly $bootstrapAssembly -HostRoot $cli
  Write-PackTiming 'payload staging and merge'
  $launcherDestination=Join-Path $cli 'launcher';New-Item -ItemType Directory -Path $launcherDestination -Force|Out-Null
  Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'packaging\remote-host\Start-dgSpyRemoteHost.ps1') -Destination $launcherDestination
  Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'packaging\remote-host\Start-dgSpyRemoteHost.cmd') -Destination $launcherDestination
  # The package has to be able to prove what it contains and where it came from. Without this the only
  # identity downstream was a hand-maintained "0.1.0" plus the hash of an apphost stub that never changes,
  # so a stale deployment was indistinguishable from a fresh one at every later stage.
  $shape=Get-PayloadShape $cli
  $commit=Get-GitValue 'rev-parse','HEAD'
  $dirty=[bool](Get-GitValue 'status','--porcelain')
  $extensionSha=(Get-FileHash -LiteralPath (Join-Path $extensionDestination 'dgSpy.Extension.x.dll') -Algorithm SHA256).Hash.ToLowerInvariant()
  $protocolSha=(Get-FileHash -LiteralPath (Join-Path $sharedBin 'dgSpy.Protocol.dll') -Algorithm SHA256).Hash.ToLowerInvariant()
  $extensionProtocolSha=(Get-FileHash -LiteralPath (Join-Path $extensionDestination 'dgSpy.Protocol.dll') -Algorithm SHA256).Hash.ToLowerInvariant()
  if($protocolSha -ne $extensionProtocolSha){ throw 'The package has different app-base and extension dgSpy.Protocol.dll files; dnSpy would silently load the app-base copy.' }
  $manifest=[ordered]@{format_version=1;runtime=$Runtime;target_framework='net10.0';shared_runtime=$true;package_format=$(if($DirectoryPackage){'directory'}else{'zip'});archive_compression=$(if($DirectoryPackage){$null}else{$CompressionLevel});created_utc=[DateTime]::UtcNow.ToString('O');entrypoint='cli/bin/dgspy.exe';gateway='cli/bin/dgSpy.Gateway.exe';host='cli/dnSpy.exe';extension_sha256=$extensionSha;protocol_sha256=$protocolSha;hooklab_payload='hooklab/'+$script:HookLabPayloadFileName;hooklab_payload_sha256=$bootstrapSha;file_count=$shape.file_count;payload_bytes=$shape.payload_bytes;git_commit=$commit;git_dirty=$dirty}
  Write-Host "dgSpy HookLab payload $($bootstrapSha.Substring(0,12)) staged at cli\hooklab"
  Write-Host "dgSpy extension $($extensionSha.Substring(0,12)) from commit $(if($commit){$commit.Substring(0,12)}else{'unknown'})$(if($dirty){' (dirty tree)'})"
  [IO.File]::WriteAllText((Join-Path $staging 'manifest.json'),(($manifest|ConvertTo-Json)+"`n"),[Text.UTF8Encoding]::new($false))
  Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'install-dgspy.ps1') -Destination $staging
  # The installer verifies the staged payload with the same script that staged it, so the package has to
  # carry it: an extracted release has no repository beside it.
  $packagingDestination=Join-Path $staging 'packaging';New-Item -ItemType Directory -Path $packagingDestination -Force|Out-Null
  Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'packaging\HookLabPayload.ps1') -Destination $packagingDestination
  if($DirectoryPackage) {
    # Source installs consume this tree on the same machine. Publish it by rename so a failed rebuild
    # leaves the prior complete directory available for an install retry.
    $packageDirectory=Join-Path $resolved "dgspy-$Runtime"
    $previousDirectory=$packageDirectory+'.previous-'+[Guid]::NewGuid().ToString('N')
    if(Test-Path -LiteralPath $packageDirectory){ Move-Item -LiteralPath $packageDirectory -Destination $previousDirectory }
    try { Move-Item -LiteralPath $staging -Destination $packageDirectory }
    catch {
      if(-not (Test-Path -LiteralPath $packageDirectory) -and (Test-Path -LiteralPath $previousDirectory)){ Move-Item -LiteralPath $previousDirectory -Destination $packageDirectory }
      throw
    }
    if(Test-Path -LiteralPath $previousDirectory) {
      try { Remove-Item -LiteralPath $previousDirectory -Recurse -Force }
      catch { Write-Warning "The new package directory is complete, but its predecessor could not be removed: $previousDirectory ($($_.Exception.Message))" }
    }
    Write-PackTiming 'directory publish'
    Write-Host "dgSpy package directory: $packageDirectory"
  }
  else {
    $archive=Join-Path $resolved "dgspy-$Runtime.zip"
    # Compress-Archive is exceptionally slow for this hundreds-of-megabytes, many-file payload. The
    # framework ZipFile implementation is also what the Gateway uses for remote packages. Create beside
    # the destination and rename only after success so a failed retry preserves the last complete archive.
    $temporaryArchive=$archive+'.tmp-'+[Guid]::NewGuid().ToString('N')
    $compression=[IO.Compression.CompressionLevel]::$CompressionLevel
    [IO.Compression.ZipFile]::CreateFromDirectory($staging,$temporaryArchive,$compression,$false)
    Move-Item -LiteralPath $temporaryArchive -Destination $archive -Force
    Write-PackTiming 'ZIP creation'
    Write-Host "dgSpy package: $archive"
  }
} finally {
  if($temporaryArchive -and (Test-Path -LiteralPath $temporaryArchive)){ Remove-Item -LiteralPath $temporaryArchive -Force }
  $resolvedStaging=[IO.Path]::GetFullPath($staging)
  if($resolvedStaging.StartsWith($resolved+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $resolvedStaging)){ Remove-Item -LiteralPath $resolvedStaging -Recurse -Force }
}
