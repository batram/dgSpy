param(
	[Parameter(Mandatory=$true)][ValidateSet('codex','claude')][string]$Agent,
	[string]$PackagePath,
	[string]$InstallDirectory = (Join-Path $env:LOCALAPPDATA 'Programs\dgSpyMcp'),
	# Kill whatever is running out of the install directory instead of refusing. This terminates the MCP
	# server the calling agent is talking to, so that agent loses its dgSpy tools until it is restarted.
	[switch]$Force
)

$ErrorActionPreference = 'Stop'
$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new()
$temporaryRoot = $null
$sourceRoot = $PSScriptRoot

function Invoke-WithRetry([scriptblock]$Action, [int]$Attempts = 10, [int]$DelayMs = 300) {
	for ($attempt = 1; $attempt -le $Attempts; $attempt++) {
		try { & $Action; return }
		catch {
			if ($attempt -eq $Attempts) { throw }
			Start-Sleep -Milliseconds $DelayMs
		}
	}
}

try {
	# A release archive already contains cli\bin\dgspy.exe. A repository checkout builds that same complete
	# archive first, so installation and agent registration are identical after this point.
	if (-not (Test-Path -LiteralPath (Join-Path $sourceRoot 'cli\bin\dgspy.exe') -PathType Leaf)) {
		if ([string]::IsNullOrWhiteSpace($PackagePath)) {
			$packScript = Join-Path $PSScriptRoot 'pack-dgspy.ps1'
			if (-not (Test-Path -LiteralPath $packScript -PathType Leaf)) {
				throw 'This is neither an extracted dgSpy release nor a repository checkout. Pass -PackagePath to dgspy-win-x64.zip.'
			}
			& $packScript
			if ($LASTEXITCODE) { throw "dgSpy package build failed with exit code $LASTEXITCODE." }
			$PackagePath = Join-Path $PSScriptRoot 'artifacts\dgspy\dgspy-win-x64.zip'
		}
		$resolvedPackage = [IO.Path]::GetFullPath($PackagePath)
		if (-not (Test-Path -LiteralPath $resolvedPackage -PathType Leaf)) { throw "Complete dgSpy package not found: $resolvedPackage" }
		$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ('dgspy-install-'+[Guid]::NewGuid().ToString('N'))
		New-Item -ItemType Directory -Path $temporaryRoot | Out-Null
		Expand-Archive -LiteralPath $resolvedPackage -DestinationPath $temporaryRoot
		$sourceRoot = $temporaryRoot
	}

	$sourceCli = Join-Path $sourceRoot 'cli\bin\dgspy.exe'
	$sourcePayload = Join-Path $sourceRoot 'cli\dnSpy.exe'
	if (-not (Test-Path -LiteralPath $sourceCli -PathType Leaf) -or -not (Test-Path -LiteralPath $sourcePayload -PathType Leaf)) {
		throw 'The package is incomplete: cli\bin\dgspy.exe or cli\dnSpy.exe is missing.'
	}

	$resolvedInstall = [IO.Path]::GetFullPath($InstallDirectory)
	$installParent = Split-Path -Parent $resolvedInstall
	if ([string]::IsNullOrWhiteSpace($installParent) -or $resolvedInstall.Equals([IO.Path]::GetPathRoot($resolvedInstall),[StringComparison]::OrdinalIgnoreCase)) {
		throw "Refusing unsafe install directory: $resolvedInstall"
	}
	New-Item -ItemType Directory -Path $installParent -Force | Out-Null
	$existingCli = Join-Path $resolvedInstall 'cli\bin\dgspy.exe'
	$legacyCli = Join-Path $resolvedInstall 'cli\dgspy.exe'
	if (Test-Path -LiteralPath $existingCli -PathType Leaf) { & $existingCli stop | Out-Host }
	elseif (Test-Path -LiteralPath $legacyCli -PathType Leaf) { & $legacyCli stop | Out-Host }

	# Stopping the Gateway is not enough: an agent that spawned `dgspy mcp` keeps its own process running out
	# of this directory, and Windows then fails the swap with an opaque "Access to the path is denied" that
	# reads like a permissions problem. Name the holder instead, because the fix is to close it, not to
	# elevate. Getting this wrong leaves the agent talking to the build it was supposed to replace.
	$holders = @(Get-Process -ErrorAction SilentlyContinue |
		Where-Object { $_.Path -and $_.Path.StartsWith($resolvedInstall+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) })
	if ($holders.Count -gt 0) {
		$detail = ($holders | ForEach-Object { "$($_.ProcessName) (PID $($_.Id))" }) -join ', '
		if (-not $Force) {
			throw "Cannot replace $resolvedInstall while these processes are running from it: $detail. If one of them is the MCP server your agent spawned, this install cannot be completed from inside that agent session - close the agent, or run this script from a plain terminal, then restart the agent. Pass -Force to terminate them and install anyway."
		}
		# Killed processes are named, not silently reaped. A debugger host terminated here takes its debug
		# targets with it, and an agent that loses its MCP server mid-session needs to know why.
		Write-Warning "Force: terminating processes running from the install directory: $detail"
		foreach ($holder in $holders) {
			try { Stop-Process -Id $holder.Id -Force -ErrorAction Stop } catch { throw "Failed to terminate $($holder.ProcessName) (PID $($holder.Id)): $($_.Exception.Message)" }
		}
		foreach ($holder in $holders) { $holder.WaitForExit(15000) | Out-Null }
		$stillRunning = @(Get-Process -ErrorAction SilentlyContinue |
			Where-Object { $_.Path -and $_.Path.StartsWith($resolvedInstall+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) })
		if ($stillRunning.Count -gt 0) {
			throw "Processes still hold $resolvedInstall after -Force: $(($stillRunning | ForEach-Object { "$($_.ProcessName) (PID $($_.Id))" }) -join ', ')."
		}
		Write-Host "Force: terminated $($holders.Count) process(es); the agent that spawned them must be restarted to regain dgSpy tools."
	}

	$staging = $resolvedInstall+'.staging-'+[Guid]::NewGuid().ToString('N')
	$backup = $resolvedInstall+'.previous-'+[Guid]::NewGuid().ToString('N')
	try {
		New-Item -ItemType Directory -Path $staging | Out-Null
		Copy-Item -LiteralPath (Join-Path $sourceRoot 'cli') -Destination $staging -Recurse
		foreach ($file in 'manifest.json','install-dgspy.ps1') {
			$sourceFile = Join-Path $sourceRoot $file
			if (Test-Path -LiteralPath $sourceFile -PathType Leaf) { Copy-Item -LiteralPath $sourceFile -Destination $staging }
		}
		# A process exiting does not mean Windows has released its handle on the directory yet, so the swap
		# can fail for a moment after a successful -Force kill. Retry briefly rather than failing an install
		# that is about to be possible.
		Invoke-WithRetry { if (Test-Path -LiteralPath $resolvedInstall) { Move-Item -LiteralPath $resolvedInstall -Destination $backup } }
		Invoke-WithRetry { Move-Item -LiteralPath $staging -Destination $resolvedInstall }
		if (Test-Path -LiteralPath $backup) { Remove-Item -LiteralPath $backup -Recurse -Force }
	}
	catch {
		if (-not (Test-Path -LiteralPath $resolvedInstall) -and (Test-Path -LiteralPath $backup)) { Move-Item -LiteralPath $backup -Destination $resolvedInstall }
		throw
	}
	finally {
		if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
	}

	$dgSpyExe = Join-Path $resolvedInstall 'cli\bin\dgspy.exe'
	if ($Agent -eq 'codex') {
		& $dgSpyExe configure codex --apply
		if ($LASTEXITCODE) { throw "Codex MCP registration failed with exit code $LASTEXITCODE." }
	}
	else {
		$claudeCommand = Get-Command claude -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
		if ($null -eq $claudeCommand) { throw "Claude Code CLI was not found. Install Claude Code, then rerun '$($MyInvocation.MyCommand.Name) claude'." }
		try { & $claudeCommand.Source mcp remove dgspy --scope user 2>$null | Out-Null } catch { }
		& $claudeCommand.Source mcp add dgspy --scope user -- $dgSpyExe mcp
		if ($LASTEXITCODE) { throw "Claude MCP registration failed with exit code $LASTEXITCODE." }
	}

	& $dgSpyExe help | Out-Null
	if ($LASTEXITCODE) { throw "dgSpy CLI verification failed with exit code $LASTEXITCODE." }

	# "Installed" has to be a claim about content. An exit code from `help` proves a CLI runs, not that the
	# tree that landed is the tree that was packed, and the extension assembly is the file whose staleness
	# is invisible at every later stage. Verifying it here fails the install instead of the debugger.
	$installedManifest = Join-Path $resolvedInstall 'manifest.json'
	if (Test-Path -LiteralPath $installedManifest -PathType Leaf) {
		$manifest = Get-Content -LiteralPath $installedManifest -Raw | ConvertFrom-Json
		$extensionDll = Join-Path $resolvedInstall 'cli\bin\Extensions\dgSpy\dgSpy.Extension.x.dll'
		if (-not (Test-Path -LiteralPath $extensionDll -PathType Leaf)) { throw "The installed payload has no dgSpy extension assembly: $extensionDll" }
		$actual = (Get-FileHash -LiteralPath $extensionDll -Algorithm SHA256).Hash.ToLowerInvariant()
		if ($manifest.PSObject.Properties.Name -contains 'extension_sha256' -and $manifest.extension_sha256) {
			if ($actual -ne $manifest.extension_sha256) {
				throw "The installed extension does not match the package manifest (installed $actual, packaged $($manifest.extension_sha256)). The copy is incomplete or the package was modified."
			}
		}
		# Coarse, but it catches the interrupted or partially-copied tree that an exact hash of one assembly
		# cannot see. The gateway does the authoritative whole-tree hash when it deploys.
		if ($manifest.PSObject.Properties.Name -contains 'file_count' -and $manifest.file_count) {
			$installedFiles = @(Get-ChildItem -LiteralPath (Join-Path $resolvedInstall 'cli') -File -Recurse)
			$installedBytes = [int64](($installedFiles | Measure-Object -Property Length -Sum).Sum)
			if ($installedFiles.Count -ne $manifest.file_count -or $installedBytes -ne $manifest.payload_bytes) {
				throw "The installed payload is incomplete: $($installedFiles.Count) files / $installedBytes bytes, packaged $($manifest.file_count) / $($manifest.payload_bytes)."
			}
		}
		$provenance = "commit $(if ($manifest.git_commit) { $manifest.git_commit.Substring(0,12) } else { 'unknown' })"
		if ($manifest.git_dirty) { $provenance += ' (built from a dirty tree)' }
		Write-Host "Verified installed extension $($actual.Substring(0,12)) - $provenance"
	}
	else { Write-Warning 'This package predates payload verification: the installed tree cannot prove what it contains.' }

	Write-Host "dgSpy is installed and registered for $Agent."
	Write-Host 'The CLI will start the Gateway on first MCP use. Restart the agent, then say: Use dgspy and go local.'
}
finally {
	if ($null -ne $temporaryRoot -and (Test-Path -LiteralPath $temporaryRoot)) { Remove-Item -LiteralPath $temporaryRoot -Recurse -Force }
}
