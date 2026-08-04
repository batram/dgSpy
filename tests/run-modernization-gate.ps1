param(
	[ValidateSet('Shared','CorDebug','Unity','Full')]
	[string]$Stage = 'Shared',
	[switch]$SkipHostBuild,
	[switch]$UpdateSnapshots
)

$ErrorActionPreference = 'Stop'
$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new()
$repoRoot = Split-Path $PSScriptRoot -Parent

function Invoke-Checked {
	param([string]$Label, [scriptblock]$Command)
	Write-Host "== $Label ==" -ForegroundColor Cyan
	& $Command
	if ($LASTEXITCODE) { throw "$Label failed with exit code $LASTEXITCODE" }
}

$locking = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object {
	$_.Name -in @('dotnet.exe','MSBuild.exe','dnSpy.exe','testhost.exe') -and
	($_.ExecutablePath -like "$repoRoot*" -or $_.CommandLine -like "*$repoRoot*")
})
if ($locking.Count) {
	$details = ($locking | ForEach-Object { "$($_.Name):$($_.ProcessId)" }) -join ', '
	throw "Refusing to build while output-locking processes are running: $details"
}

Push-Location $repoRoot
try {
	if (-not $SkipHostBuild) {
		Invoke-Checked 'dnSpy net48 build' {
			.\build.ps1 netframework -MSBuildPath 'C:\Program Files (x86)\Microsoft Visual Studio\2019\Community\MSBuild\Current\Bin\amd64\MSBuild.exe'
		}
	}
	Invoke-Checked 'dgSpy build and deploy' { .\build-dgspy.ps1 }

	if ($UpdateSnapshots) {
		$env:DGSPY_UPDATE_SNAPSHOTS = '1'
		$env:DGSPY_SNAPSHOT_DIR = Join-Path $repoRoot 'tests\dgSpy.Gateway.Tests\Snapshots'
	}
	try {
		Invoke-Checked 'Protocol tests' { dotnet test tests\dgSpy.Protocol.Tests\dgSpy.Protocol.Tests.csproj -c Release --nologo -v:minimal }
		Invoke-Checked 'Gateway tests and contract snapshots' { dotnet test tests\dgSpy.Gateway.Tests\dgSpy.Gateway.Tests.csproj -c Release --nologo -v:minimal }
		Invoke-Checked 'Extension tests' { dotnet test tests\dgSpy.Extension.Tests\dgSpy.Extension.Tests.csproj -c Release --nologo -v:minimal }
	}
	finally {
		Remove-Item Env:DGSPY_UPDATE_SNAPSHOTS -ErrorAction SilentlyContinue
		Remove-Item Env:DGSPY_SNAPSHOT_DIR -ErrorAction SilentlyContinue
	}

	if ($Stage -in @('CorDebug','Full')) {
		Invoke-Checked 'CorDebug live smoke' { .\tests\run-milestone1-smoke.ps1 }
	}
	if ($Stage -in @('Unity','Full')) {
		Write-Host '== start isolated Unity debugger host ==' -ForegroundColor Cyan
		$unityHostId = & .\ps_scratch\Start-DnSpyPhase6Uch.ps1
		try {
			Invoke-Checked 'Unity read-only modernization smoke' { .\tests\run-uch-modernization-smoke.ps1 }
		}
		finally {
			Stop-Process -Id $unityHostId -Force -ErrorAction SilentlyContinue
		}
	}
}
finally { Pop-Location }
