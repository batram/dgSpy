param([ValidateSet('mcp','start','stop','status','doctor')][string]$Command='start')
$ErrorActionPreference='Stop'
# See build.ps1: keep MSBuild from leaving reusable worker nodes holding bin/obj handles.
$env:MSBUILDDISABLENODEREUSE='1'
$cli=Join-Path $PSScriptRoot 'dgSpy.Cli\bin\Release\net10.0\dgspy.dll'
if(-not (Test-Path -LiteralPath $cli)) { dotnet build (Join-Path $PSScriptRoot 'dgSpy.Cli\dgSpy.Cli.csproj') -c Release --nologo -v:minimal; if($LASTEXITCODE){ throw "dgspy CLI build failed: $LASTEXITCODE" } }
$env:DGSPY_GATEWAY_PATH=Join-Path $PSScriptRoot 'dgSpy.Gateway\bin\Release\net10.0\dgSpy.Gateway.dll'
& dotnet $cli $Command
exit $LASTEXITCODE
