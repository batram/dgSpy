param([ValidateSet('Release')][string]$Configuration='Release')
$ErrorActionPreference='Stop'
# Some agent hosts inherit both Path and PATH. MSBuild's VC task copies the raw environment into a
# case-insensitive dictionary and fails before starting CL.exe. Temporarily keep the canonical Path
# entry only, and always restore the caller's process environment because this script is dot-sourced
# into the invoking PowerShell process even when launched with &.
$savedProcessPath=[Environment]::GetEnvironmentVariable('Path',[EnvironmentVariableTarget]::Process)
$project=Join-Path $PSScriptRoot '..\HookLab\HookLab.NativeBootstrap\HookLab.NativeBootstrap.vcxproj'
$candidates=@(
	'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\amd64\MSBuild.exe',
	'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\amd64\MSBuild.exe',
	'C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\amd64\MSBuild.exe'
)
$msbuild=$candidates|Where-Object { Test-Path -LiteralPath $_ -PathType Leaf }|Select-Object -First 1
if(-not $msbuild){ throw 'MSBuild with the Visual C++ x64 toolchain was not found.' }
try {
	[Environment]::SetEnvironmentVariable('PATH',$null,[EnvironmentVariableTarget]::Process)
	& $msbuild $project /nologo /m:1 /p:Configuration=$Configuration /p:Platform=x64 /v:minimal /clp:ErrorsOnly
	if($LASTEXITCODE){ throw "HookLab native initializer build failed with exit code $LASTEXITCODE." }
	$output=Join-Path (Split-Path $project) "bin\$Configuration\HookLab.NativeBootstrap.x64.dll"
	if(-not (Test-Path -LiteralPath $output -PathType Leaf)){ throw "HookLab native initializer output is missing: $output" }
	Write-Output $output
}
finally {
	[Environment]::SetEnvironmentVariable('Path',$savedProcessPath,[EnvironmentVariableTarget]::Process)
}
