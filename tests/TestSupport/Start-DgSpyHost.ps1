<#
.SYNOPSIS
	Start an isolated dnSpy host with the dgSpy extension and wait for its RPC port.

.DESCRIPTION
	Replaces ps_scratch\Start-DnSpyPhase6Uch.ps1, which is gitignored and hardcodes an absolute
	net48 path under one developer's home directory. That is why -Stage Unity cannot run from a
	clean clone: the gate calls a script that is not in the repository and would point at the wrong
	machine if it were.

	Resolves the host from the repository and the requested framework instead, so a clean clone and
	a CI runner both work.

	Returns the process id. Callers are responsible for stopping it, normally in a finally block.

	ASCII-only by policy: an em dash in a .ps1 parses as a stray quote under CP1252.

.EXAMPLE
	$hostId = & tests\TestSupport\Start-DgSpyHost.ps1 -RpcPort 7351
	try { ... } finally { Stop-Process -Id $hostId -Force -ErrorAction SilentlyContinue }
#>
[CmdletBinding()]
param(
	# Port the extension serves RPC on. Must not collide with a gateway port or another host.
	[Parameter(Mandatory)][int]$RpcPort,

	# net10 is the shipping host. net48 is the retained fallback baseline.
	[ValidateSet('net10.0-windows','net48')][string]$TargetFramework = 'net10.0-windows',

	[int]$TimeoutSeconds = 40
)

$ErrorActionPreference = 'Stop'
$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new()

$repoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent

# The net10 host is a self-contained publish, so its runtime tree is the publish directory. Both
# layouts put dnSpy.exe at the root with the runtime and Extensions\ under bin\.
$dnSpyDir = if ($TargetFramework -eq 'net48') {
	Join-Path $repoRoot 'dnSpy\dnSpy\bin\Release\net48'
} else {
	Join-Path $repoRoot 'dnSpy\dnSpy\bin\Release\net10.0-windows\win-x64\publish'
}
$dnSpyExe = Join-Path $dnSpyDir 'dnSpy.exe'
if (-not (Test-Path $dnSpyExe)) {
	throw "dnSpy host not found at $dnSpyExe. Build it first: .\build.ps1 net-x64 -NoMsbuild (or .\build.ps1 netframework for net48), then .\build-dgspy.ps1."
}

# A host with no extension deployed starts happily and then answers nothing, which reads as a
# debugger fault rather than a missing build step.
$extension = Join-Path $dnSpyDir 'bin\Extensions\dgSpy\dgSpy.Extension.x.dll'
if (-not (Test-Path $extension)) {
	throw "The dgSpy extension is not deployed to $dnSpyDir. Run .\build-dgspy.ps1 -TargetFramework $TargetFramework."
}

$env:DGSPY_RPC_PORT = "$RpcPort"

# --dgspy-no-window-activation is a dgSpy patch to dnSpy. -WindowStyle Hidden only sets the initial
# show state, and the debugger calls SetForegroundWindow + Window.Activate on every stop, which
# steals focus from whatever the user is typing. Nothing outside the process can prevent that.
# --multiple allows this host to coexist with a dnSpy the developer already has open.
$process = Start-Process -FilePath $dnSpyExe -WorkingDirectory $dnSpyDir -WindowStyle Hidden -PassThru `
	-ArgumentList '--multiple','--dgspy-no-window-activation'

# Readiness is a TCP connect to dnSpy's own RPC port, which is a plain request/response server and
# is unharmed by a probe. Do NOT copy this to a Mono/Unity debugger agent port: connecting to an
# agent without completing the DWP handshake wedges it, and connecting while the runtime is still
# suspended awaiting its first debugger kills the player outright. Observe those with
# Get-NetTCPConnection -State Listen instead.
$deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
$connected = $false
do {
	Start-Sleep -Milliseconds 250
	try {
		$client = [Net.Sockets.TcpClient]::new()
		$connected = $client.ConnectAsync('127.0.0.1',$RpcPort).Wait(500) -and $client.Connected
		$client.Dispose()
	} catch { $connected = $false }
} until ($connected -or $process.HasExited -or [DateTime]::UtcNow -gt $deadline)

if (-not $connected) {
	$detail = if ($process.HasExited) { "the host exited with code $($process.ExitCode)" } else { "the host is running but never listened on $RpcPort" }
	Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
	throw "dnSpy RPC did not come up within $TimeoutSeconds s: $detail. Host: $dnSpyExe"
}

$process.Id
