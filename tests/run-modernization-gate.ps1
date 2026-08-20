param(
	[ValidateSet('Shared','CorDebug','Unity','MonoTarget','Full')]
	[string]$Stage = 'Shared',
	[switch]$SkipHostBuild,
	[string]$LayoutRoot,
	[switch]$UpdateSnapshots
)

$ErrorActionPreference = 'Stop'
# See build.ps1. This gate refuses to run when leftover build nodes hold repo output, so the
# nodes this run spawns must not survive it either.
$env:MSBUILDDISABLENODEREUSE = '1'
$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new()
$repoRoot = Split-Path $PSScriptRoot -Parent

# The Unity stages used to refuse anything but net48, for two reasons that no longer hold. The host
# launcher was ps_scratch\Start-DnSpyPhase6Uch.ps1, gitignored and hardcoding an absolute net48 path,
# so a net10 run started a net48 dnSpy with no extension deployed and failed like a Unity regression;
# tests\TestSupport\Start-DgSpyHost.ps1 now resolves the host per framework and refuses up front if
# the extension is missing. And Unity/UCH had no acceptance evidence on net10, which it now does:
# the net10 host was driven against a live patched Unity player over attach_endpoint, covering
# modules, threads, call stacks, frames, evaluation and expansion.
#
# So the stages run on the shipping host by default, like everything else.

function Invoke-Checked {
	param([string]$Label, [scriptblock]$Command)
	Write-Host "== $Label ==" -ForegroundColor Cyan
	& $Command
	if ($LASTEXITCODE) { throw "$Label failed with exit code $LASTEXITCODE" }
}

# Processes running from an agent worktree under .claude\worktrees hold that worktree's own build
# output, not this tree's; matching them here refused gate runs for a concurrent session that could
# not actually lock anything this gate builds.
$worktrees = Join-Path $repoRoot '.claude\worktrees'
$locking = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object {
	$_.Name -in @('dotnet.exe','MSBuild.exe','dnSpy.exe','testhost.exe') -and
	($_.ExecutablePath -like "$repoRoot*" -or $_.CommandLine -like "*$repoRoot*") -and
	-not ($_.ExecutablePath -like "$worktrees*" -or $_.CommandLine -like "*$worktrees*")
})
if ($locking.Count) {
	$details = ($locking | ForEach-Object { "$($_.Name):$($_.ProcessId)" }) -join ', '
	throw "Refusing to build while output-locking processes are running: $details"
}

Push-Location $repoRoot
try {
	$completedLayout = if ([string]::IsNullOrWhiteSpace($LayoutRoot)) { $null } else { [IO.Path]::GetFullPath($LayoutRoot) }
	if ($completedLayout) {
		dotnet run --project Build\DgSpyTool\DgSpyTool.csproj -c Release -- verify --layout $completedLayout
		if ($LASTEXITCODE) { throw "Configured layout verification failed with exit code $LASTEXITCODE" }
		$env:DGSPY_LAYOUT_ROOT = $completedLayout
	}
	if (-not $SkipHostBuild) {
		$buildId = "gate-$([Guid]::NewGuid().ToString('N'))"
		$completedLayout = Join-Path $repoRoot "artifacts\layouts\$buildId"
		Invoke-Checked 'immutable build, composition, and package pipeline' {
			dotnet run --project Build\DgSpyTool\DgSpyTool.csproj -c Release -- pipeline --repo $repoRoot --artifacts (Join-Path $repoRoot 'artifacts') --build-id $buildId --layout $completedLayout
		}
		$env:DGSPY_LAYOUT_ROOT = $completedLayout
	}
	Invoke-Checked 'PowerShell host-launcher tests' { .\tests\TestSupport\Start-DgSpyHost.Tests.ps1 }
	Invoke-Checked 'immutable pipeline tests' { dotnet test tests\DgSpyTool.Tests\DgSpyTool.Tests.csproj -c Release --nologo -v:minimal }

	if ($UpdateSnapshots) {
		$env:DGSPY_UPDATE_SNAPSHOTS = '1'
		$env:DGSPY_SNAPSHOT_DIR = Join-Path $repoRoot 'tests\dgSpy.Gateway.Tests\Snapshots'
	}
	try {
		Invoke-Checked 'Protocol tests' { dotnet test tests\dgSpy.Protocol.Tests\dgSpy.Protocol.Tests.csproj -c Release --nologo -v:minimal }
		Invoke-Checked 'Contract boundary tests' { dotnet test tests\dgSpy.Contracts.Tests\dgSpy.Contracts.Tests.csproj -c Release --nologo -v:minimal }
		Invoke-Checked 'HookLab probe tests' { dotnet test tests\HookLab.Probe.Tests\HookLab.Probe.Tests.csproj -c Release --nologo -v:minimal }
		Invoke-Checked 'HookLab transport tests' { dotnet test tests\HookLab.Transport.Tests\HookLab.Transport.Tests.csproj -c Release --nologo -v:minimal }
		Invoke-Checked 'HookLab bootstrap tests' { dotnet test tests\HookLab.Bootstrap.Tests\HookLab.Bootstrap.Tests.csproj -c Release --nologo -v:minimal }
		Invoke-Checked 'HookLab watcher tests' { dotnet test tests\HookLab.Watcher.Tests\HookLab.Watcher.Tests.csproj -c Release --nologo -v:minimal }
		Invoke-Checked 'HookLab watcher companion tests' { dotnet test tests\HookLab.Watcher.Companion.Tests\HookLab.Watcher.Companion.Tests.csproj -c Release --nologo -v:minimal }
		Invoke-Checked 'Gateway tests and contract snapshots' { dotnet test tests\dgSpy.Gateway.Tests\dgSpy.Gateway.Tests.csproj -c Release --nologo -v:minimal }
		Invoke-Checked 'Extension tests' { dotnet test tests\dgSpy.Extension.Tests\dgSpy.Extension.Tests.csproj -c Release --nologo -v:minimal }
		# Must run after the publish above: it loads the published assemblies and
		# reproduces dnSpy's MEF composition. dnSpy only validates that under
		# Debug.Assert, so the Release build we ship drops unsatisfiable parts in
		# silence and no other check in this gate can see it.
		$previousPublishBin = $env:DGSPY_PUBLISH_BIN
		try {
			$env:DGSPY_PUBLISH_BIN = if ($completedLayout) {
				Join-Path $completedLayout 'bin'
			} else {
				Join-Path $repoRoot 'dnSpy\dnSpy\bin\Release\net10.0-windows\win-x64\publish\bin'
			}
			Invoke-Checked 'Composition tests' { dotnet test tests\dgSpy.Composition.Tests\dgSpy.Composition.Tests.csproj -c Release --nologo -v:minimal }
		}
		finally {
			$env:DGSPY_PUBLISH_BIN = $previousPublishBin
		}
	}
	finally {
		Remove-Item Env:DGSPY_UPDATE_SNAPSHOTS -ErrorAction SilentlyContinue
		Remove-Item Env:DGSPY_SNAPSHOT_DIR -ErrorAction SilentlyContinue
	}

	if ($Stage -in @('CorDebug','Full')) {
		Invoke-Checked 'CorDebug live smoke' { .\tests\run-milestone1-smoke.ps1 }
		# Nothing else builds the fixture in Release: run-milestone1-smoke.ps1 builds it Debug, and both
		# live legs below require bin\Release\net48. It happens to exist on the machine this was written
		# on, which is exactly why the gap was invisible until a clean clone hit the first Test-Path.
		Invoke-Checked 'CorDebug fixture (Release)' { dotnet build tests\TestTargets\Milestone1Target\Milestone1Target.csproj -c Release -f net48 --nologo -v:minimal }
		Invoke-Checked 'CoreCLR debugger fixture (Release)' { dotnet build tests\TestTargets\CoreClrDebuggerTarget\CoreClrDebuggerTarget.csproj -c Release -f net10.0 --nologo -v:minimal }
		Invoke-Checked 'CoreCLR debugger live smoke' { .\tests\run-coreclr-debugger-smoke.ps1 }
		Invoke-Checked 'CoreCLR HookLab live smoke' { .\tests\run-coreclr-hooklab-smoke.ps1 }
		Invoke-Checked 'HookLab interactive fixture (Release)' { dotnet build tests\TestTargets\HookLabInteractiveTarget\HookLabInteractiveTarget.csproj -c Release -f net48 --nologo -v:minimal }
		# T08 shipped with no live leg: every one of the nine defects T08b fixes lived in the
		# dnSpy-facing half, which no test entered. This runs a real atomic action against the CorDebug
		# fixture on whichever host framework the gate is exercising.
		Invoke-Checked 'Atomic-action live smoke' { .\tests\run-atomic-action-smoke.ps1 }
		Invoke-Checked 'HookLab install live smoke' { .\tests\run-hooklab-install-smoke.ps1 }
		# Road 1 subslice 8. Every leg above runs the debugger and the target as one user in the default
		# application domain - the arrangement that hid five defects until a live IIS worker found them,
		# and three more on 2026-08-20. This one runs the target as a second local account and hooks code
		# that exists only in a second application domain.
		#
		# It needs the dgspy-fixture account, which a hosted runner will not have, so it is skipped with a
		# named reason rather than failing the gate - and it refuses to fall back to a same-user run,
		# because a cross-identity gate that quietly runs as one identity is the gap it exists to close.
		Invoke-Checked 'Cross-identity HookLab fixture (Release)' { dotnet build tests\TestTargets\HookLabCrossIdentityTarget\HookLabCrossIdentityTarget.csproj -c Release --nologo -v:minimal }
		Invoke-Checked 'Cross-identity CoreCLR HookLab fixture (Release)' { dotnet build tests\TestTargets\HookLabCrossIdentityCoreTarget\HookLabCrossIdentityCoreTarget.csproj -c Release --nologo -v:minimal }
		$crossIdentityElevated = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
		$crossIdentityAccount = [bool](Get-LocalUser -Name 'dgspy-fixture' -ErrorAction SilentlyContinue)
		if ($crossIdentityAccount -and $crossIdentityElevated) {
			Invoke-Checked 'Cross-identity, cross-domain HookLab live smoke' { .\tests\run-hooklab-cross-identity-smoke.ps1 }
			# CoreCLR has one application domain, so this leg covers the identity axis only. That is the
			# runtime's shape, not a reduced test: there is no second domain to enter.
			Invoke-Checked 'Cross-identity CoreCLR HookLab live smoke' { .\tests\run-hooklab-cross-identity-coreclr-smoke.ps1 }
		}
		else {
			# Both reasons are named, because "skipped" without one is how a gate quietly stops covering
			# the case it was added for.
			$why = @()
			if (-not $crossIdentityAccount) { $why += 'the dgspy-fixture account does not exist' }
			if (-not $crossIdentityElevated) { $why += 'this gate is not elevated, and attaching to another account needs SeDebugPrivilege' }
			Write-Host ("SKIP  Cross-identity, cross-domain HookLab live smoke: " + ($why -join '; ') + ". See the header of tests\run-hooklab-cross-identity-smoke.ps1.") -ForegroundColor Yellow
		}
	}
	# Needs a listening uch-debug-target player, which this repo neither builds nor ships: it takes a
	# Unity editor and a licence, which hosted runners do not have. Launch it first with the target
	# repo's tools\Launch-Target.ps1. Left out of Full for that reason -- a stage that cannot run
	# unattended would make Full unrunnable rather than thorough.
	if ($Stage -eq 'MonoTarget') {
		# A server=y Unity agent accepts one debugger connection per player launch. This local stage can
		# therefore exercise only the primary debugger smoke against the supplied player. CI launches a
		# fresh player for each of the debugger, search, and scan-cursor scripts in a three-way matrix.
		Invoke-Checked 'Mono/Unity live smoke' { .\tests\run-mono-target-smoke.ps1 }
	}
	if ($Stage -eq 'Unity') {
		Write-Host '== start isolated Unity debugger host ==' -ForegroundColor Cyan
		# Never 7351: an installed dgSpy holding 7351 makes this stage silently test the WRONG host -
		# the second dnSpy cannot bind the port, but the readiness probe is a bare TCP connect and
		# reaches the installed host instead. Start-DgSpyHost exports DGSPY_RPC_PORT, and
		# Invoke-DgSpyRpc honors it, so the smoke follows automatically.
		#
		# 0, not the dedicated 7367 this used to be: Windows reserves large TCP ranges for
		# Hyper-V/WinNAT, 7367 landed inside one, and dnSpy then started without ever listening. The
		# ranges move between boots, so a free ephemeral port is the only choice that is always right -
		# and it settles the installed-dgSpy collision above at the same time.
		$unityRpcPort = if ($env:DGSPY_RPC_PORT) { [int]$env:DGSPY_RPC_PORT } else { 0 }
		$unityHostId = & .\tests\TestSupport\Start-DgSpyHost.ps1 -RpcPort $unityRpcPort
		try {
			Invoke-Checked 'Unity read-only modernization smoke' { .\tests\run-uch-modernization-smoke.ps1 }
		}
		finally {
			Stop-Process -Id $unityHostId -Force -ErrorAction SilentlyContinue
		}
	}
}
finally { Pop-Location }
