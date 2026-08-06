param([string]$Configuration='Release')
$ErrorActionPreference='Stop'
$OutputEncoding=[Console]::OutputEncoding=[Text.UTF8Encoding]::new()
$scratch=Join-Path ([IO.Path]::GetTempPath()) ('dgspy-agent-workflow-'+[Guid]::NewGuid().ToString('N'))
$state=Join-Path $scratch 'state'
$cli=Join-Path $PSScriptRoot "..\dgSpy.Cli\bin\$Configuration\net10.0\dgspy.dll"
$gateway=Join-Path $PSScriptRoot "..\dgSpy.Gateway\bin\$Configuration\net10.0\dgSpy.Gateway.dll"
try {
  dotnet build (Join-Path $PSScriptRoot '..\dgSpy.Gateway\dgSpy.Gateway.csproj') -c $Configuration --nologo -v:minimal
  if($LASTEXITCODE){throw "Gateway build failed: $LASTEXITCODE"}
  dotnet build (Join-Path $PSScriptRoot '..\dgSpy.Cli\dgSpy.Cli.csproj') -c $Configuration --nologo -v:minimal
  if($LASTEXITCODE){throw "CLI build failed: $LASTEXITCODE"}
  $env:DGSPY_STATE_ROOT=$state
  $env:DGSPY_INSTALL_ROOT=Join-Path $scratch 'install'
  $env:DGSPY_PACKAGE_ROOT=Join-Path $scratch 'packages'
  $env:DGSPY_GATEWAY_PATH=$gateway
  & dotnet $cli start
  if($LASTEXITCODE){throw "start failed: $LASTEXITCODE"}
  & dotnet $cli status
  if($LASTEXITCODE){throw "status failed: $LASTEXITCODE"}
  & dotnet $cli doctor
  if($LASTEXITCODE){throw "doctor failed: $LASTEXITCODE"}
  $initialize='{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"workflow-smoke","version":"1"}}}'
  $response=$initialize|& dotnet $cli mcp
  if($LASTEXITCODE -or $response -notmatch '"instructions"' -or $response -notmatch '"resources"'){throw 'stdio MCP discovery failed'}
  Write-Host 'Verified zero-state startup, status, doctor, and stdio MCP discovery.'
} finally {
  if(Test-Path -LiteralPath $cli){ & dotnet $cli stop | Out-Host }
  $resolvedScratch=[IO.Path]::GetFullPath($scratch)
  $tempRoot=[IO.Path]::GetFullPath([IO.Path]::GetTempPath())
  if($resolvedScratch.StartsWith($tempRoot,[StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $resolvedScratch)){Remove-Item -LiteralPath $resolvedScratch -Recurse -Force}
}
