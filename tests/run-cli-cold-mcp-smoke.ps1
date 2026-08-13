param([string]$Configuration='Release')
$ErrorActionPreference='Stop'
$OutputEncoding=[Console]::OutputEncoding=[Text.UTF8Encoding]::new()
$cli=Join-Path $PSScriptRoot "..\dgSpy.Cli\bin\$Configuration\net10.0\dgspy.dll"
$scratch=Join-Path ([IO.Path]::GetTempPath()) ('dgspy-cli-cold-mcp-'+[Guid]::NewGuid().ToString('N'))
try {
  $env:DGSPY_STATE_ROOT=Join-Path $scratch 'state'
  $env:DGSPY_URL='http://127.0.0.1:1/mcp'
  $env:DGSPY_GATEWAY_PATH=Join-Path $scratch 'gateway-must-not-start.exe'
  $initialize='{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"cold-smoke","version":"1"}}}'
  $tools='{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}'
  $timer=[Diagnostics.Stopwatch]::StartNew()
  $initializeResponse=$initialize|& dotnet $cli mcp
  if($LASTEXITCODE){throw "Cold MCP initialize failed: $LASTEXITCODE"}
  $toolsResponse=$tools|& dotnet $cli mcp
  $timer.Stop()
  $response=@($initializeResponse)+@($toolsResponse)
  $body=$response -join ''
  if($LASTEXITCODE){throw "Cold MCP tools/list failed: $LASTEXITCODE"}
  if($body -notmatch '"instructions"' -or $body -notmatch '"tools"'){throw 'Cold MCP discovery response was incomplete.'}
  if($timer.Elapsed.TotalSeconds -ge 5){throw "Cold MCP discovery waited for Gateway startup: $($timer.Elapsed)"}
  if(Test-Path -LiteralPath (Join-Path $env:DGSPY_STATE_ROOT 'gateway.pid')){throw 'Catalog discovery started the Gateway.'}
  Write-Host "Verified Gateway-independent MCP discovery in $($timer.Elapsed.TotalMilliseconds) ms."
} finally {
  $resolvedScratch=[IO.Path]::GetFullPath($scratch)
  $tempRoot=[IO.Path]::GetFullPath([IO.Path]::GetTempPath())
  if($resolvedScratch.StartsWith($tempRoot,[StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $resolvedScratch)){Remove-Item -LiteralPath $resolvedScratch -Recurse -Force}
}
