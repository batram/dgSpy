param(
	[Parameter(Mandatory=$true)][ValidateSet('codex','claude')][string]$Agent,
	# A release ZIP or an already-extracted/package directory. Repository installs populate this internally.
	[string]$PackagePath,
	[string]$InstallDirectory = (Join-Path $env:LOCALAPPDATA 'Programs\dgSpyMcp'),
	# Update only the bundled dnSpy host payload. The running dgspy MCP process and Gateway live in
	# cli\bin and are deliberately left untouched; launch_local_host performs versioned activation later.
	[switch]$HostOnly,
	# Kill whatever is running out of the install directory instead of refusing. This terminates the MCP
	# server the calling agent is talking to, so that agent loses its dgSpy tools until it is restarted.
	[switch]$Force
)

$ErrorActionPreference = 'Stop'
$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new()
[void][Reflection.Assembly]::LoadWithPartialName('System.IO.Compression.FileSystem')
# Both a repository checkout and an extracted release carry this beside the installer: pack-dgspy.ps1
# copies it into the package root. One verifier, so the installed tree is checked the same way the
# package was staged.
$hookLabPayloadHelper = Join-Path $PSScriptRoot 'packaging\HookLabPayload.ps1'
if (-not (Test-Path -LiteralPath $hookLabPayloadHelper -PathType Leaf)) {
	throw "This installer is missing packaging\HookLabPayload.ps1 beside it, so it cannot verify the HookLab payload: $hookLabPayloadHelper"
}
. $hookLabPayloadHelper
$temporaryRoot = $null
$sourceRoot = $PSScriptRoot
$packageBuiltHere = $false
$installStopwatch = [Diagnostics.Stopwatch]::StartNew()
$lastPhase = $installStopwatch.Elapsed

function Invoke-WithRetry([scriptblock]$Action, [int]$Attempts = 10, [int]$DelayMs = 300) {
	for ($attempt = 1; $attempt -le $Attempts; $attempt++) {
		try { & $Action; return }
		catch {
			if ($attempt -eq $Attempts) { throw }
			Start-Sleep -Milliseconds $DelayMs
		}
	}
}

function Write-InstallTiming([string]$Phase) {
	$now = $installStopwatch.Elapsed
	Write-Host ("dgSpy install timing: {0} {1:n1}s (total {2:n1}s)" -f $Phase,($now-$script:lastPhase).TotalSeconds,$now.TotalSeconds)
	$script:lastPhase = $now
}

function Copy-HostPayload([string]$SourceCli, [string]$InstalledCli) {
	if (-not (Test-Path -LiteralPath $InstalledCli -PathType Container)) {
		throw "Host-only update requires an existing complete dgSpy installation: $InstalledCli"
	}

	# These are the only files at cli\bin's root owned by dnSpy. dgspy.exe, dgSpy.Gateway.exe and their
	# shared runtime stay byte-for-byte unchanged so an MCP process can keep running from this directory.
	$sourceBin = Join-Path $SourceCli 'bin'
	$installedBin = Join-Path $InstalledCli 'bin'
	$sourceProtocol = Join-Path $sourceBin 'dgSpy.Protocol.dll'
	$installedProtocol = Join-Path $installedBin 'dgSpy.Protocol.dll'
	if (-not (Test-Path -LiteralPath $sourceProtocol -PathType Leaf) -or -not (Test-Path -LiteralPath $installedProtocol -PathType Leaf)) {
		throw 'Host-only update requires dgSpy.Protocol.dll in both the package and installed control plane.'
	}
	if ((Get-FileHash -LiteralPath $sourceProtocol -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $installedProtocol -Algorithm SHA256).Hash) {
		throw "Host-only update cannot install a protocol/schema change while the MCP Gateway is running. No host files were changed. Run the full installer and restart Codex: .\install-dgspy.ps1 -Agent $Agent -PackagePath '$sourceRoot' -Force"
	}
	foreach ($file in Get-ChildItem -LiteralPath $sourceBin -File -Filter 'dnSpy*') {
		Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $installedBin $file.Name) -Force
	}
	foreach ($file in Get-ChildItem -LiteralPath $SourceCli -File -Filter 'dnSpy*.exe') {
		Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $InstalledCli $file.Name) -Force
	}

	# Extension and payload directories are host-owned and are not loaded by the CLI or Gateway. Replace
	# their contents so removed assemblies do not linger and silently compose into later host versions.
	foreach ($relative in 'bin\Extensions','hooklab','launcher') {
		$source = Join-Path $SourceCli $relative
		$destination = Join-Path $InstalledCli $relative
		if (-not (Test-Path -LiteralPath $source -PathType Container)) { throw "Host payload is incomplete: $relative" }
		$staging = $destination+'.staging-'+[Guid]::NewGuid().ToString('N')
		$backup = $destination+'.previous-'+[Guid]::NewGuid().ToString('N')
		try {
			Copy-Item -LiteralPath $source -Destination $staging -Recurse
			if (Test-Path -LiteralPath $destination) { Move-Item -LiteralPath $destination -Destination $backup }
			Move-Item -LiteralPath $staging -Destination $destination
			if (Test-Path -LiteralPath $backup) { Remove-Item -LiteralPath $backup -Recurse -Force }
		}
		catch {
			if (-not (Test-Path -LiteralPath $destination) -and (Test-Path -LiteralPath $backup)) { Move-Item -LiteralPath $backup -Destination $destination }
			throw
		}
		finally {
			if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
		}
	}
}

function Update-HostPayloadManifest([string]$SourceRoot, [string]$InstalledRoot) {
	$sourceManifestPath = Join-Path $SourceRoot 'manifest.json'
	$installedManifestPath = Join-Path $InstalledRoot 'manifest.json'
	if (-not (Test-Path -LiteralPath $sourceManifestPath -PathType Leaf) -or -not (Test-Path -LiteralPath $installedManifestPath -PathType Leaf)) {
		throw 'Host-only update requires both the new package manifest and the installed package manifest.'
	}
	$sourceManifest = Get-Content -LiteralPath $sourceManifestPath -Raw | ConvertFrom-Json
	$installedManifest = Get-Content -LiteralPath $installedManifestPath -Raw | ConvertFrom-Json
	$installedManifest.extension_sha256 = $sourceManifest.extension_sha256
	if ($sourceManifest.PSObject.Properties.Name -contains 'protocol_sha256') { $installedManifest | Add-Member -NotePropertyName protocol_sha256 -NotePropertyValue $sourceManifest.protocol_sha256 -Force }
	$installedManifest.hooklab_payload_sha256 = $sourceManifest.hooklab_payload_sha256
	$installedFiles = @(Get-ChildItem -LiteralPath (Join-Path $InstalledRoot 'cli') -File -Recurse)
	$installedManifest.file_count = $installedFiles.Count
	$installedManifest.payload_bytes = [int64](($installedFiles | Measure-Object -Property Length -Sum).Sum)
	$installedManifest | Add-Member -NotePropertyName host_update -NotePropertyValue ([ordered]@{
		created_utc = [DateTime]::UtcNow.ToString('O')
		git_commit = $sourceManifest.git_commit
		git_dirty = $sourceManifest.git_dirty
	}) -Force
	[IO.File]::WriteAllText($installedManifestPath,(($installedManifest | ConvertTo-Json -Depth 6) + "`n"),[Text.UTF8Encoding]::new($false))
}

function Assert-PackageContent([string]$PackageRoot) {
	$manifestPath = Join-Path $PackageRoot 'manifest.json'
	if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw "The package has no manifest.json and cannot prove its contents: $PackageRoot" }
	$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
	$cliRoot = Join-Path $PackageRoot 'cli'
	$extension = Join-Path $cliRoot 'bin\Extensions\dgSpy\dgSpy.Extension.x.dll'
	$rootProtocol = Join-Path $cliRoot 'bin\dgSpy.Protocol.dll'
	$extensionProtocol = Join-Path $cliRoot 'bin\Extensions\dgSpy\dgSpy.Protocol.dll'
	foreach ($required in $extension,$rootProtocol,$extensionProtocol) { if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "The package is missing required assembly: $required" } }
	if (-not $manifest.extension_sha256 -or -not $manifest.protocol_sha256 -or -not $manifest.hooklab_payload_sha256) { throw 'The package manifest lacks an extension, protocol, or HookLab digest; rebuild it with the current pack-dgspy.ps1.' }
	$extensionHash = (Get-FileHash -LiteralPath $extension -Algorithm SHA256).Hash.ToLowerInvariant()
	$rootProtocolHash = (Get-FileHash -LiteralPath $rootProtocol -Algorithm SHA256).Hash.ToLowerInvariant()
	$extensionProtocolHash = (Get-FileHash -LiteralPath $extensionProtocol -Algorithm SHA256).Hash.ToLowerInvariant()
	if ($extensionHash -ne $manifest.extension_sha256) { throw "The package extension digest is $extensionHash; manifest records $($manifest.extension_sha256)." }
	if ($rootProtocolHash -ne $manifest.protocol_sha256 -or $extensionProtocolHash -ne $manifest.protocol_sha256) { throw "The package protocol contract is inconsistent (root $rootProtocolHash, extension $extensionProtocolHash, manifest $($manifest.protocol_sha256))." }
	$files = @(Get-ChildItem -LiteralPath $cliRoot -File -Recurse)
	$bytes = [int64](($files | Measure-Object -Property Length -Sum).Sum)
	if ($files.Count -ne $manifest.file_count -or $bytes -ne $manifest.payload_bytes) { throw "The package tree is incomplete: $($files.Count) files / $bytes bytes, manifest records $($manifest.file_count) / $($manifest.payload_bytes)." }
	$null = Test-HookLabPayload -HostRoot $cliRoot -ExpectedSha256 $manifest.hooklab_payload_sha256
	return $manifest
}

try {
	# A release archive already contains cli\bin\dgspy.exe. A repository checkout builds the same complete
	# package first, so installation and agent registration are identical after this point.
	if (-not (Test-Path -LiteralPath (Join-Path $sourceRoot 'cli\bin\dgspy.exe') -PathType Leaf)) {
		if ([string]::IsNullOrWhiteSpace($PackagePath)) {
			$packScript = Join-Path $PSScriptRoot 'pack-dgspy.ps1'
			if (-not (Test-Path -LiteralPath $packScript -PathType Leaf)) {
				throw 'This is neither an extracted dgSpy release nor a repository checkout. Pass -PackagePath to dgspy-win-x64.zip or an extracted package directory.'
			}
			# A local source install consumes this tree immediately, so do not create and extract an archive
			# that never crosses a network. Keep it outside artifacts\dgspy so it cannot be mistaken for the
			# optimally compressed release artifact.
			$localPackageDirectory = Join-Path $PSScriptRoot 'artifacts\dgspy-local'
			& $packScript -OutputDirectory $localPackageDirectory -DirectoryPackage
			if ($LASTEXITCODE) { throw "dgSpy package build failed with exit code $LASTEXITCODE." }
			$PackagePath = Join-Path $localPackageDirectory 'dgspy-win-x64'
			$packageBuiltHere = $true
			Write-InstallTiming 'package build'
		}
		$resolvedPackage = [IO.Path]::GetFullPath($PackagePath)
		if (Test-Path -LiteralPath $resolvedPackage -PathType Container) { $sourceRoot = $resolvedPackage }
		elseif (Test-Path -LiteralPath $resolvedPackage -PathType Leaf) {
			$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ('dgspy-install-'+[Guid]::NewGuid().ToString('N'))
			New-Item -ItemType Directory -Path $temporaryRoot | Out-Null
			[IO.Compression.ZipFile]::ExtractToDirectory($resolvedPackage,$temporaryRoot)
			$sourceRoot = $temporaryRoot
			Write-InstallTiming 'package extraction'
		}
		else { throw "Complete dgSpy package not found: $resolvedPackage" }
	}

	$sourceCli = Join-Path $sourceRoot 'cli\bin\dgspy.exe'
	$sourcePayload = Join-Path $sourceRoot 'cli\dnSpy.exe'
	if (-not (Test-Path -LiteralPath $sourceCli -PathType Leaf) -or -not (Test-Path -LiteralPath $sourcePayload -PathType Leaf)) {
		throw 'The package is incomplete: cli\bin\dgspy.exe or cli\dnSpy.exe is missing.'
	}
	# Validate before stopping a control plane or touching an installed host. A bad package must be a cheap
	# refusal, not a rollback exercise after the user's debugger and MCP connection have already ended.
	$null = Assert-PackageContent -PackageRoot $sourceRoot

	$resolvedInstall = [IO.Path]::GetFullPath($InstallDirectory)
	$installParent = Split-Path -Parent $resolvedInstall
	if ([string]::IsNullOrWhiteSpace($installParent) -or $resolvedInstall.Equals([IO.Path]::GetPathRoot($resolvedInstall),[StringComparison]::OrdinalIgnoreCase)) {
		throw "Refusing unsafe install directory: $resolvedInstall"
	}
	if ($HostOnly) {
		if ($Force) { throw '-HostOnly never stops the MCP or Gateway, so it cannot be combined with -Force.' }
		$installedCli = Join-Path $resolvedInstall 'cli'
		$protected = @('bin\dgspy.exe','bin\dgSpy.Gateway.exe')
		$before = @{}
		foreach ($relative in $protected) {
			$path = Join-Path $installedCli $relative
			if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Host-only update requires the installed control plane: $path" }
			$before[$relative] = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
		}
		Copy-HostPayload -SourceCli (Join-Path $sourceRoot 'cli') -InstalledCli $installedCli
		Update-HostPayloadManifest -SourceRoot $sourceRoot -InstalledRoot $resolvedInstall
		foreach ($relative in $protected) {
			$after = (Get-FileHash -LiteralPath (Join-Path $installedCli $relative) -Algorithm SHA256).Hash
			if ($after -ne $before[$relative]) { throw "Host-only update changed protected control-plane file: $relative" }
		}
		$extension = Join-Path $installedCli 'bin\Extensions\dgSpy\dgSpy.Extension.x.dll'
		$payloadSha = Test-HookLabPayload -HostRoot $installedCli
		Write-InstallTiming 'host payload update'
		Write-Host "Updated bundled dnSpy host payload; extension $(((Get-FileHash -LiteralPath $extension -Algorithm SHA256).Hash).Substring(0,12).ToLowerInvariant()), HookLab $($payloadSha.Substring(0,12))."
		Write-Host 'The CLI, Gateway, MCP registration, and running MCP process were left untouched.'
		Write-Host 'Activate it with launch_local_host replace=true (this closes only the running dnSpy host and ends its debug sessions).'
		return
	}
	# Policy is mutable administrator state, not package payload. Create explicit disabled defaults once
	# and leave the file untouched on every later upgrade.
	$policyDirectory = Join-Path $env:LOCALAPPDATA 'dgSpy'
	$policyFile = Join-Path $policyDirectory 'capability-policy.json'
	if (-not (Test-Path -LiteralPath $policyFile -PathType Leaf)) {
		New-Item -ItemType Directory -Path $policyDirectory -Force | Out-Null
		$disabledPolicy = [ordered]@{ format_version=1; defaults=[ordered]@{ runtime_hooks=$false; custom_hook_code=$false; hook_export=$false }; entries=@() }
		[IO.File]::WriteAllText($policyFile,(($disabledPolicy | ConvertTo-Json -Depth 4) + "`n"),[Text.UTF8Encoding]::new($false))
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
			try { Stop-Process -Id $holder.Id -Force -ErrorAction Stop }
			catch {
				# Exiting between enumeration and Stop-Process is the successful outcome, not an install
				# failure. Only suppress the race when that exact PID is now absent; access denied and a
				# still-live process remain hard failures.
				if ($null -ne (Get-Process -Id $holder.Id -ErrorAction SilentlyContinue)) {
					throw "Failed to terminate $($holder.ProcessName) (PID $($holder.Id)): $($_.Exception.Message)"
				}
			}
		}
		foreach ($holder in $holders) { try { $holder.WaitForExit(15000) | Out-Null } catch { } }
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
		# Carried into the installation so the copy of install-dgspy.ps1 that lands there can still verify
		# the payload if it is ever re-run in place.
		$sourcePackaging = Join-Path $sourceRoot 'packaging\HookLabPayload.ps1'
		if (Test-Path -LiteralPath $sourcePackaging -PathType Leaf) {
			New-Item -ItemType Directory -Path (Join-Path $staging 'packaging') -Force | Out-Null
			Copy-Item -LiteralPath $sourcePackaging -Destination (Join-Path $staging 'packaging')
		}
		$null = Assert-PackageContent -PackageRoot $staging
		Write-InstallTiming 'installation staging copy'
		# A process exiting does not mean Windows has released its handle on the directory yet, so the swap
		# can fail for a moment after a successful -Force kill. Retry briefly rather than failing an install
		# that is about to be possible.
		Invoke-WithRetry { if (Test-Path -LiteralPath $resolvedInstall) { Move-Item -LiteralPath $resolvedInstall -Destination $backup } }
		Invoke-WithRetry { Move-Item -LiteralPath $staging -Destination $resolvedInstall }
		Write-InstallTiming 'directory swap'
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
		if ($manifest.PSObject.Properties.Name -contains 'protocol_sha256' -and $manifest.protocol_sha256) {
			$rootProtocol = Join-Path $resolvedInstall 'cli\bin\dgSpy.Protocol.dll'
			$extensionProtocol = Join-Path $resolvedInstall 'cli\bin\Extensions\dgSpy\dgSpy.Protocol.dll'
			$rootHash = (Get-FileHash -LiteralPath $rootProtocol -Algorithm SHA256).Hash.ToLowerInvariant()
			$extensionHash = (Get-FileHash -LiteralPath $extensionProtocol -Algorithm SHA256).Hash.ToLowerInvariant()
			if ($rootHash -ne $manifest.protocol_sha256 -or $extensionHash -ne $manifest.protocol_sha256) {
				throw "The installed protocol contract is inconsistent (root $rootHash, extension $extensionHash, packaged $($manifest.protocol_sha256))."
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
		# The HookLab payload is the file whose absence would only surface when a payload action tried to
		# inject it. Verify it here, from the bytes rather than from the record: the installed manifest and
		# the payload manifest are separate records, so a tampered payload has to defeat both.
		if ($manifest.PSObject.Properties.Name -contains 'hooklab_payload_sha256' -and $manifest.hooklab_payload_sha256) {
			$payloadSha = Test-HookLabPayload -HostRoot (Join-Path $resolvedInstall 'cli') -ExpectedSha256 $manifest.hooklab_payload_sha256
			Write-Host "Verified installed HookLab payload $($payloadSha.Substring(0,12)) - delivered as bytes, not reachable from any extension search path"
		}
		else { Write-Warning 'This package carries no HookLab payload: runtime hooking has nothing to inject. Repackage with a current pack-dgspy.ps1.' }
		$provenance = "commit $(if ($manifest.git_commit) { $manifest.git_commit.Substring(0,12) } else { 'unknown' })"
		if ($manifest.git_dirty) { $provenance += ' (built from a dirty tree)' }
		Write-Host "Verified installed extension $($actual.Substring(0,12)) - $provenance"
	}
	else { Write-Warning 'This package predates payload verification: the installed tree cannot prove what it contains.' }

	Write-Host "dgSpy is installed and registered for $Agent."
	Write-Host 'The CLI will start the Gateway on first MCP use. Restart the agent, then say: Use dgspy and go local.'
	if (Test-Path -LiteralPath $backup) {
		try { Invoke-WithRetry { Remove-Item -LiteralPath $backup -Recurse -Force } }
		catch { Write-Warning "The new installation is verified, but the previous installation could not be removed: $backup ($($_.Exception.Message))" }
	}
	Write-InstallTiming 'previous installation cleanup'
}
catch {
	# The previous tree remains available until every content check, registration command, and CLI smoke
	# succeeds. Restore it on any late failure instead of leaving a newly swapped but unusable install.
	if ($backup -and (Test-Path -LiteralPath $backup -PathType Container)) {
		$failed = $resolvedInstall+'.failed-'+[Guid]::NewGuid().ToString('N')
		try {
			if (Test-Path -LiteralPath $resolvedInstall) { Move-Item -LiteralPath $resolvedInstall -Destination $failed }
			Move-Item -LiteralPath $backup -Destination $resolvedInstall
			if (Test-Path -LiteralPath $failed) { Remove-Item -LiteralPath $failed -Recurse -Force }
			Write-Warning 'The install failed after the directory swap; the previous installation was restored.'
		}
		catch { Write-Warning "Automatic rollback also failed: $($_.Exception.Message). Previous tree: $backup" }
	}
	if ($packageBuiltHere -and $PackagePath -and (Test-Path -LiteralPath $PackagePath)) {
		Write-Warning "The completed package was preserved. Retry the install without rebuilding: .\install-dgspy.ps1 $Agent -PackagePath '$PackagePath'$(if ($Force) { ' -Force' })"
	}
	throw
}
finally {
	if ($null -ne $temporaryRoot -and (Test-Path -LiteralPath $temporaryRoot)) { Remove-Item -LiteralPath $temporaryRoot -Recurse -Force }
}
