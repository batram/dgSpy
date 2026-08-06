param([ValidateSet('mcp','start','stop','status','doctor')][string]$Command='start')
$ErrorActionPreference='Stop'
$cli=Join-Path $PSScriptRoot 'dgSpy.Cli\bin\Release\net7.0\dgspy.dll'
if(-not (Test-Path -LiteralPath $cli)) { dotnet build (Join-Path $PSScriptRoot 'dgSpy.Cli\dgSpy.Cli.csproj') -c Release --nologo -v:minimal; if($LASTEXITCODE){ throw "dgspy CLI build failed: $LASTEXITCODE" } }
$env:DGSPY_SOURCE_ROOT=$PSScriptRoot
$env:DGSPY_GATEWAY_PATH=Join-Path $PSScriptRoot 'dgSpy.Gateway\bin\Release\net7.0\dgSpy.Gateway.dll'
& dotnet $cli $Command
exit $LASTEXITCODE
