param([string]$Configuration='Release',[string]$Runtime='win-x64',[string]$OutputDirectory="$PSScriptRoot\artifacts\dgspy")
$ErrorActionPreference='Stop'
$resolved=[IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $resolved -Force|Out-Null
$staging=Join-Path $resolved ('.staging-'+[Guid]::NewGuid().ToString('N'))
$cli=Join-Path $staging 'cli'
$gateway=Join-Path $cli 'gateway'
try {
  dotnet publish (Join-Path $PSScriptRoot 'dgSpy.Cli\dgSpy.Cli.csproj') -c $Configuration -r $Runtime --self-contained true -o $cli --nologo -v:minimal
  if($LASTEXITCODE){ throw "CLI publish failed: $LASTEXITCODE" }
  dotnet publish (Join-Path $PSScriptRoot 'dgSpy.Gateway\dgSpy.Gateway.csproj') -c $Configuration -r $Runtime --self-contained true -o $gateway --nologo -v:minimal
  if($LASTEXITCODE){ throw "Gateway publish failed: $LASTEXITCODE" }
  $manifest=[ordered]@{format_version=1;runtime=$Runtime;created_utc=[DateTime]::UtcNow.ToString('O');entrypoint='cli/dgspy.exe';gateway='cli/gateway/dgSpy.Gateway.exe'}
  [IO.File]::WriteAllText((Join-Path $staging 'manifest.json'),(($manifest|ConvertTo-Json)+"`n"),[Text.UTF8Encoding]::new($false))
  $archive=Join-Path $resolved "dgspy-$Runtime.zip"
  Compress-Archive -Path (Join-Path $staging 'cli'),(Join-Path $staging 'manifest.json') -DestinationPath $archive -Force
  Write-Host "dgSpy package: $archive"
} finally {
  $resolvedStaging=[IO.Path]::GetFullPath($staging)
  if($resolvedStaging.StartsWith($resolved+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $resolvedStaging)){ Remove-Item -LiteralPath $resolvedStaging -Recurse -Force }
}
