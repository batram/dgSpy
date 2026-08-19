# Road 1 subslice 6 prototype runner. Builds the fixture, the domain-only app assembly, the payload and
# the native prototype, starts the fixture, injects, and prints what came back.
#
# ASCII-only. Runs the fixture with no window; nothing here opens a GUI.
[CmdletBinding()]
param([switch]$KeepArtifacts)
$ErrorActionPreference = 'Stop'
$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new()

$here = $PSScriptRoot
$repo = Split-Path (Split-Path $here -Parent) -Parent
$artifacts = Join-Path $here 'artifacts'
if (Test-Path $artifacts) { Remove-Item $artifacts -Recurse -Force }
New-Item -ItemType Directory -Force -Path $artifacts | Out-Null

function Step([string]$Name) { Write-Host "== $Name ==" -ForegroundColor Cyan }

Step 'build managed pieces'
foreach ($project in 'App\AppDomainProof.App.csproj','Embedded\AppDomainProof.Embedded.csproj','Managed\AppDomainProof.Payload.csproj','Fixture\AppDomainProof.Fixture.csproj') {
    dotnet build (Join-Path $here $project) -c Release --nologo -v:q | Out-Null
    if ($LASTEXITCODE) { throw "build failed: $project" }
}
# One directory, because the payload is read from beside the native DLL and the fixture's application
# base has to contain the app assembly.
foreach ($pattern in 'App\bin\Release\net48\AppDomainProof.App.dll','Embedded\bin\Release\net48\AppDomainProof.Embedded.dll','Managed\bin\Release\net48\AppDomainProof.Payload.dll','Fixture\bin\Release\net48\AppDomainProof.Fixture.exe') {
    Copy-Item (Join-Path $here $pattern) $artifacts -Force
}

Step 'build native prototype'
$vcvars = @(
    'C:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvars64.bat',
    'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat',
    'C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat',
    'C:\Program Files (x86)\Microsoft Visual Studio\2019\BuildTools\VC\Auxiliary\Build\vcvars64.bat'
) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $vcvars) { throw 'No x64 Visual C++ build environment was found.' }
Write-Host "  toolchain: $vcvars"
$source = Join-Path $here 'Native\Bootstrap.cpp'
$outDll = Join-Path $artifacts 'AppDomainProof.Native.dll'
# The type library lives with the framework; #import needs it on the include path.
$tlbDir = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
$build = Join-Path $env:TEMP ("appdomainproof-build-" + [Guid]::NewGuid().ToString('N') + ".cmd")
@"
@echo off
call "$vcvars" >nul
if errorlevel 1 exit /b 1
cd /d "$artifacts"
cl.exe /nologo /std:c++17 /EHsc /W4 /O2 /MT /I"$tlbDir" /LD "$source" /Fe:"$outDll" /link mscoree.lib
exit /b %errorlevel%
"@ | Set-Content -LiteralPath $build -Encoding ASCII
& cmd.exe /c $build
$nativeExit = $LASTEXITCODE
Remove-Item $build -Force -ErrorAction SilentlyContinue
if ($nativeExit -ne 0) { throw "native prototype build failed with exit code $nativeExit" }

Step 'start the multi-domain fixture'
$fixture = Join-Path $artifacts 'AppDomainProof.Fixture.exe'
$start = New-Object Diagnostics.ProcessStartInfo
$start.FileName = $fixture
$start.Arguments = '"' + $artifacts + '"'
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.RedirectStandardOutput = $true
$process = [Diagnostics.Process]::Start($start)
try {
    $factsPath = Join-Path $artifacts 'fixture-facts.txt'
    $deadline = (Get-Date).AddSeconds(20)
    while (-not (Test-Path $factsPath) -and (Get-Date) -lt $deadline -and -not $process.HasExited) { Start-Sleep -Milliseconds 100 }
    if (-not (Test-Path $factsPath)) { throw 'The fixture did not publish its facts.' }
    Get-Content $factsPath | ForEach-Object { Write-Host "  $_" }

    Step 'inject the native prototype'
    # Build once and invoke the executable, rather than 'dotnet run -- a b'. Argument forwarding through
    # dotnet run did not deliver both arguments here and the injector printed its usage; running the
    # produced exe removes that layer instead of guessing at its quoting rules.
    $injector = Join-Path $repo 'tests\HookLab.NativeBootstrapProof\HookLab.NativeBootstrapProof.Injector\HookLab.NativeBootstrapProof.Injector.csproj'
    dotnet build $injector -c Release --nologo -v:q | Out-Null
    if ($LASTEXITCODE) { throw 'injector build failed' }
    $injectorExe = Join-Path (Split-Path $injector) 'bin\Release\net10.0-windows\HookLab.NativeBootstrapProof.Injector.exe'
    if (-not (Test-Path $injectorExe)) { throw "injector executable not found: $injectorExe" }
    $targetProcessId = $process.Id
    & $injectorExe $targetProcessId $outDll
    if ($LASTEXITCODE) { throw "injection failed with exit code $LASTEXITCODE" }

    Step 'results'
    $nativeProof = Join-Path $artifacts 'native-domain-proof.txt'
    $domainProof = Join-Path $artifacts 'domain-proof.txt'
    $deadline = (Get-Date).AddSeconds(20)
    while (-not (Test-Path $nativeProof) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 100 }
    foreach ($proof in @($nativeProof, $domainProof, (Join-Path $artifacts 'in-memory-proof.txt'), (Join-Path $artifacts 'marker-facts.txt'), (Join-Path $artifacts 'domain-proof-error.txt'))) {
        if (Test-Path $proof) {
            Write-Host ("-- " + (Split-Path $proof -Leaf) + " --") -ForegroundColor Yellow
            # The native prototype writes wide characters with no BOM, so it has to be read as Unicode
            # explicitly; the default read renders it one letter per line with NULs between.
            $encoding = if ((Split-Path $proof -Leaf) -eq 'native-domain-proof.txt') { [Text.Encoding]::Unicode } else { [Text.Encoding]::UTF8 }
            foreach ($line in ([IO.File]::ReadAllText($proof, $encoding) -split "`r?`n")) {
                if (-not [string]::IsNullOrWhiteSpace($line)) { Write-Host "  $line" }
            }
        }
    }
    if (-not (Test-Path $domainProof)) { Write-Host '-- domain-proof.txt was never written --' -ForegroundColor Red }
}
finally {
    Set-Content -LiteralPath (Join-Path $artifacts 'stop.txt') -Value 'stop' -Encoding ASCII
    if (-not $process.WaitForExit(5000)) { $process.Kill($true) }
    $process.Dispose()
    if (-not $KeepArtifacts) { Write-Host "artifacts: $artifacts" }
}
