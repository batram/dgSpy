param([string]$Configuration='Release',[string]$Runtime='win-x64',[string]$OutputDirectory="$PSScriptRoot\artifacts\dgspy")
$ErrorActionPreference='Stop'
# See build.ps1: keep MSBuild from leaving reusable worker nodes holding bin/obj handles.
$env:MSBUILDDISABLENODEREUSE='1'
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
  dotnet build (Join-Path $PSScriptRoot 'Extensions\dgSpy.Extension\dgSpy.Extension.csproj') -c $Configuration -f net10.0-windows --nologo -v:minimal
  if($LASTEXITCODE){ throw "Remote extension build failed: $LASTEXITCODE" }
  dotnet publish (Join-Path $PSScriptRoot 'dgSpy.Cli\dgSpy.Cli.csproj') -c $Configuration -r $Runtime --self-contained true -o $cliPublish --nologo -v:minimal
  if($LASTEXITCODE){ throw "CLI publish failed: $LASTEXITCODE" }
  dotnet publish (Join-Path $PSScriptRoot 'dgSpy.Gateway\dgSpy.Gateway.csproj') -c $Configuration -r $Runtime --self-contained true -o $gatewayPublish --nologo -v:minimal
  if($LASTEXITCODE){ throw "Gateway publish failed: $LASTEXITCODE" }
  $hostPublish=Join-Path $PSScriptRoot 'dnSpy\dnSpy\bin\Release\net10.0-windows\win-x64\publish'
  $extensionOutput=Join-Path $PSScriptRoot "Extensions\dgSpy.Extension\bin\$Configuration\net10.0-windows"
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
  foreach($file in 'dgSpy.Extension.x.dll','dgSpy.Extension.x.pdb','dgSpy.Protocol.dll','dgSpy.Protocol.pdb'){
    Copy-Item -LiteralPath (Join-Path $extensionOutput $file) -Destination $extensionDestination
  }
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
  $manifest=[ordered]@{format_version=1;runtime=$Runtime;target_framework='net10.0';shared_runtime=$true;created_utc=[DateTime]::UtcNow.ToString('O');entrypoint='cli/bin/dgspy.exe';gateway='cli/bin/dgSpy.Gateway.exe';host='cli/dnSpy.exe';extension_sha256=$extensionSha;file_count=$shape.file_count;payload_bytes=$shape.payload_bytes;git_commit=$commit;git_dirty=$dirty}
  Write-Host "dgSpy extension $($extensionSha.Substring(0,12)) from commit $(if($commit){$commit.Substring(0,12)}else{'unknown'})$(if($dirty){' (dirty tree)'})"
  [IO.File]::WriteAllText((Join-Path $staging 'manifest.json'),(($manifest|ConvertTo-Json)+"`n"),[Text.UTF8Encoding]::new($false))
  Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'install-dgspy.ps1') -Destination $staging
  $archive=Join-Path $resolved "dgspy-$Runtime.zip"
  Compress-Archive -Path (Join-Path $staging 'cli'),(Join-Path $staging 'manifest.json'),(Join-Path $staging 'install-dgspy.ps1') -DestinationPath $archive -Force
  Write-Host "dgSpy package: $archive"
} finally {
  $resolvedStaging=[IO.Path]::GetFullPath($staging)
  if($resolvedStaging.StartsWith($resolved+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $resolvedStaging)){ Remove-Item -LiteralPath $resolvedStaging -Recurse -Force }
}
