param(
	[Parameter(Mandatory=$true)][ValidateSet('codex','claude')][string]$Agent,
	[string]$PackagePath,
	[string]$InstallDirectory = (Join-Path $env:LOCALAPPDATA 'Programs\dgSpyMcp')
)

$ErrorActionPreference = 'Stop'
$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new()
$temporaryRoot = $null
$sourceRoot = $PSScriptRoot

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

	$staging = $resolvedInstall+'.staging-'+[Guid]::NewGuid().ToString('N')
	$backup = $resolvedInstall+'.previous-'+[Guid]::NewGuid().ToString('N')
	try {
		New-Item -ItemType Directory -Path $staging | Out-Null
		Copy-Item -LiteralPath (Join-Path $sourceRoot 'cli') -Destination $staging -Recurse
		foreach ($file in 'manifest.json','install-dgspy.ps1') {
			$sourceFile = Join-Path $sourceRoot $file
			if (Test-Path -LiteralPath $sourceFile -PathType Leaf) { Copy-Item -LiteralPath $sourceFile -Destination $staging }
		}
		if (Test-Path -LiteralPath $resolvedInstall) { Move-Item -LiteralPath $resolvedInstall -Destination $backup }
		Move-Item -LiteralPath $staging -Destination $resolvedInstall
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
	Write-Host "dgSpy is installed and registered for $Agent."
	Write-Host 'The CLI will start the Gateway on first MCP use. Restart the agent, then say: Use dgspy and go local.'
}
finally {
	if ($null -ne $temporaryRoot -and (Test-Path -LiteralPath $temporaryRoot)) { Remove-Item -LiteralPath $temporaryRoot -Recurse -Force }
}
