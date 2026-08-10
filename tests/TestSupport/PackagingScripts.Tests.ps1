$ErrorActionPreference = 'Stop'
$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new()

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$packPath = Join-Path $repoRoot 'pack-dgspy.ps1'
$installPath = Join-Path $repoRoot 'install-dgspy.ps1'
$layoutTestPath = Join-Path $repoRoot 'tools\Test-DgSpyPackagedHostLayout.ps1'
$poisonedRootFixture = Join-Path $PSScriptRoot 'Fixtures\poisoned-net48-root-files.txt'

function Read-ScriptAst([string]$ScriptPath) {
	$tokens = $null
	$errors = $null
	$ast = [Management.Automation.Language.Parser]::ParseFile($ScriptPath, [ref]$tokens, [ref]$errors)
	if ($errors.Count -ne 0) { throw "$ScriptPath has PowerShell parse errors: $($errors.Message -join '; ')" }
	return $ast
}

$packAst = Read-ScriptAst $packPath
$installAst = Read-ScriptAst $installPath
$packText = $packAst.Extent.Text
$installText = $installAst.Extent.Text

$compressionParameter = $packAst.ParamBlock.Parameters |
	Where-Object { $_.Name.VariablePath.UserPath -eq 'CompressionLevel' }
if ($null -eq $compressionParameter -or $compressionParameter.DefaultValue.Extent.Text -ne "'Optimal'") {
	throw 'Release packaging must default to Optimal compression.'
}
if ($packText -notmatch 'Join-Path\s+\$resolved\s+"dgspy-\$Runtime\.zip"' -or
	$packText -notmatch 'ZipFile\]::CreateFromDirectory') {
	throw 'The default packer must retain the portable dgspy-$Runtime.zip output.'
}
if ($packText -notmatch 'if\s*\(\$DirectoryPackage\)' -or
	$packText -notmatch 'Join-Path\s+\$resolved\s+"dgspy-\$Runtime"') {
	throw 'DirectoryPackage must publish the complete unarchived package beside release artifacts.'
}
if ($installText -notmatch '&\s+\$packScript\s+-OutputDirectory\s+\$localPackageDirectory\s+-DirectoryPackage' -or
	$installText -notmatch "artifacts\\dgspy-local") {
	throw 'Repository installs must request the isolated directory package, not overwrite release output.'
}
if ($installText -notmatch 'Test-Path\s+-LiteralPath\s+\$resolvedPackage\s+-PathType\s+Container' -or
	$installText -notmatch 'Test-Path\s+-LiteralPath\s+\$resolvedPackage\s+-PathType\s+Leaf') {
	throw 'PackagePath must continue accepting both extracted directories and release ZIP files.'
}
if ($installText -notmatch 'ZipFile\]::ExtractToDirectory') {
	throw 'Release ZIP installation must retain the native extraction path.'
}

$fixtureRoot = Join-Path $PSScriptRoot 'bin\packaged-layout-contract'
if (Test-Path -LiteralPath $fixtureRoot) { Remove-Item -LiteralPath $fixtureRoot -Recurse -Force }
try {
	New-Item -ItemType Directory -Path (Join-Path $fixtureRoot 'bin') -Force | Out-Null
	Set-Content -LiteralPath (Join-Path $fixtureRoot 'dnSpy.exe') -Value ''
	Set-Content -LiteralPath (Join-Path $fixtureRoot 'dnSpy.Console.exe') -Value ''
	Set-Content -LiteralPath (Join-Path $fixtureRoot 'bin\dnSpy.Contracts.DnSpy.dll') -Value ''
	& $layoutTestPath -HostRoot $fixtureRoot -TargetFramework net10.0-windows

	Set-Content -LiteralPath (Join-Path $fixtureRoot 'dnSpy.Contracts.DnSpy.dll') -Value ''
	try {
		& $layoutTestPath -HostRoot $fixtureRoot -TargetFramework net10.0-windows
		throw 'Layout validator accepted a root dnSpy.Contracts.DnSpy.dll.'
	}
	catch {
		if ($_.Exception.Message -notmatch 'AppDirectories\.BinDirectory') { throw }
	}
	Remove-Item -LiteralPath (Join-Path $fixtureRoot 'dnSpy.Contracts.DnSpy.dll') -Force

	# Preserve the exact root listing captured from the T07-poisoned net48 package. This catches
	# both the BinDirectory-defining contract copy and its broader transitive dependency closure.
	Remove-Item -LiteralPath $fixtureRoot -Recurse -Force
	New-Item -ItemType Directory -Path (Join-Path $fixtureRoot 'bin') -Force | Out-Null
	Set-Content -LiteralPath (Join-Path $fixtureRoot 'bin\dnSpy.Contracts.DnSpy.dll') -Value ''
	foreach ($name in Get-Content -LiteralPath $poisonedRootFixture) {
		Set-Content -LiteralPath (Join-Path $fixtureRoot $name) -Value ''
	}
	try {
		& $layoutTestPath -HostRoot $fixtureRoot -TargetFramework net48
		throw 'Layout validator accepted the captured poisoned net48 root.'
	}
	catch {
		if ($_.Exception.Message -notmatch 'AppDirectories\.BinDirectory') { throw }
	}

	Remove-Item -LiteralPath $fixtureRoot -Recurse -Force
	New-Item -ItemType Directory -Path (Join-Path $fixtureRoot 'bin') -Force | Out-Null
	Set-Content -LiteralPath (Join-Path $fixtureRoot 'dnSpy.exe') -Value ''
	Set-Content -LiteralPath (Join-Path $fixtureRoot 'dnSpy.Console.exe') -Value ''
	Set-Content -LiteralPath (Join-Path $fixtureRoot 'bin\dnSpy.Contracts.DnSpy.dll') -Value ''

	Set-Content -LiteralPath (Join-Path $fixtureRoot 'stray.dll') -Value ''
	try {
		& $layoutTestPath -HostRoot $fixtureRoot -TargetFramework net10.0-windows
		throw 'Layout validator accepted an unexpected root file.'
	}
	catch {
		if ($_.Exception.Message -notmatch 'unexpected files') { throw }
	}
}
finally {
	if (Test-Path -LiteralPath $fixtureRoot) { Remove-Item -LiteralPath $fixtureRoot -Recurse -Force }
}

Write-Host 'PASSED  release ZIP, local directory, and packaged host layout contracts'

# ---------------------------------------------------------------------------------------------------
# P01: the HookLab payload. One file, staged under <host root>\hooklab, digest verified from the bytes.
# ---------------------------------------------------------------------------------------------------

$remotePackPath = Join-Path $repoRoot 'pack-remote-host.ps1'
$buildPath = Join-Path $repoRoot 'build-dgspy.ps1'
$payloadHelperPath = Join-Path $repoRoot 'packaging\HookLabPayload.ps1'
$remotePackText = (Read-ScriptAst $remotePackPath).Extent.Text
$buildText = (Read-ScriptAst $buildPath).Extent.Text
$null = Read-ScriptAst $payloadHelperPath

# Every producer stages through the one helper, so there is a single layout rule to keep. A second
# hand-rolled copy is how the installed layout and the developer layout drifted apart before.
foreach ($producer in @(
	@{ Name = 'pack-dgspy.ps1'; Text = $packText },
	@{ Name = 'pack-remote-host.ps1'; Text = $remotePackText },
	@{ Name = 'build-dgspy.ps1'; Text = $buildText })) {
	if ($producer.Text -notmatch 'packaging\\HookLabPayload\.ps1' -or $producer.Text -notmatch 'Write-HookLabPayload') {
		throw "$($producer.Name) must stage the HookLab payload through packaging\HookLabPayload.ps1."
	}
}
if ($packText -notmatch 'Write-HookLabPayload\s+-BootstrapAssembly\s+\$bootstrapAssembly\s+-HostRoot\s+\$cli') {
	throw 'pack-dgspy.ps1 must stage the payload inside cli\, the tree the installer copies and the Gateway deploys as one version.'
}
if ($packText -notmatch 'hooklab_payload_sha256=\$bootstrapSha') {
	throw 'pack-dgspy.ps1 must record the payload digest in the package manifest, independently of the payload manifest.'
}
if ($installText -notmatch 'Test-HookLabPayload\s+-HostRoot\s+\(Join-Path\s+\$resolvedInstall\s+''cli''\)\s+-ExpectedSha256\s+\$manifest\.hooklab_payload_sha256') {
	throw 'install-dgspy.ps1 must verify the installed payload against the package manifest digest.'
}
if ($installText -notmatch 'packaging\\HookLabPayload\.ps1') {
	throw 'install-dgspy.ps1 must load the shared payload verifier, and the package must carry it.'
}
if ($packText -notmatch 'Join-Path\s+\$staging\s+''packaging''') {
	throw 'pack-dgspy.ps1 must copy packaging\HookLabPayload.ps1 into the package, or an extracted release cannot verify itself.'
}
if ($remotePackText -notmatch 'Test-HookLabPayload\s+-HostRoot\s+\$resolvedBundle') {
	throw 'pack-remote-host.ps1 must re-verify the staged payload before it archives the bundle.'
}

. $payloadHelperPath

$bootstrapAssembly = Join-Path $repoRoot 'HookLab\HookLab.Bootstrap\bin\Release\net48\HookLab.Bootstrap.dll'
if (-not (Test-Path -LiteralPath $bootstrapAssembly -PathType Leaf)) {
	# Real bytes matter here: the digest checks are only meaningful over the assembly that actually ships.
	& dotnet build (Join-Path $repoRoot 'HookLab\HookLab.Bootstrap\HookLab.Bootstrap.csproj') -c Release --nologo -v:minimal
	if ($LASTEXITCODE) { throw "Could not build HookLab.Bootstrap for the payload tests (exit $LASTEXITCODE)." }
	if (-not (Test-Path -LiteralPath $bootstrapAssembly -PathType Leaf)) { throw "HookLab bootstrap output is still missing: $bootstrapAssembly" }
}

function Assert-PayloadFailure([scriptblock]$Action, [string]$Pattern, [string]$Because) {
	try { & $Action | Out-Null }
	catch {
		if ($_.Exception.Message -notmatch $Pattern) { throw "$Because The failure did not match '$Pattern': $($_.Exception.Message)" }
		return
	}
	throw $Because
}

$payloadFixture = Join-Path $PSScriptRoot 'bin\hooklab-payload-contract'
if (Test-Path -LiteralPath $payloadFixture) { Remove-Item -LiteralPath $payloadFixture -Recurse -Force }
try {
	# A packed host layout, not a bare directory: dnSpy.exe at the root, BinDirectory under bin\, the
	# extension in bin\Extensions\dgSpy. The reachability guard is only meaningful against this shape.
	New-Item -ItemType Directory -Path (Join-Path $payloadFixture 'bin\Extensions\dgSpy') -Force | Out-Null
	Set-Content -LiteralPath (Join-Path $payloadFixture 'dnSpy.exe') -Value ''
	Set-Content -LiteralPath (Join-Path $payloadFixture 'dnSpy.Console.exe') -Value ''
	Set-Content -LiteralPath (Join-Path $payloadFixture 'bin\dnSpy.Contracts.DnSpy.dll') -Value ''
	Set-Content -LiteralPath (Join-Path $payloadFixture 'bin\Extensions\dgSpy\dgSpy.Extension.x.dll') -Value ''

	$staged = Write-HookLabPayload -BootstrapAssembly $bootstrapAssembly -HostRoot $payloadFixture
	$expected = (Get-FileHash -LiteralPath $bootstrapAssembly -Algorithm SHA256).Hash.ToLowerInvariant()
	if ($staged -ne $expected) { throw "Staging recorded $staged for a payload whose bytes hash to $expected." }
	$payloadFile = Get-HookLabPayloadFile $payloadFixture
	if (Test-HookLabPayload -HostRoot $payloadFixture -ExpectedSha256 $expected) { } else { throw 'A freshly staged payload failed its own verification.' }
	# Staging a payload must not disturb the packaged-root invariant: hooklab\ is a directory, and the
	# root allowlist is about files.
	& $layoutTestPath -HostRoot $payloadFixture -TargetFramework net10.0-windows

	# The negative case that matters most: altered bytes are detected at use, not trusted from the record.
	$original = [IO.File]::ReadAllBytes($payloadFile)
	$tampered = [byte[]]::new($original.Length)
	[Array]::Copy($original, $tampered, $original.Length)
	$tampered[$tampered.Length - 1] = [byte](($tampered[$tampered.Length - 1] + 1) % 256)
	[IO.File]::WriteAllBytes($payloadFile, $tampered)
	Assert-PayloadFailure { Test-HookLabPayload -HostRoot $payloadFixture } 'does not match its manifest digest' `
		'Verification accepted a payload whose bytes were altered.'

	# And tampering with both records together still fails, because the installer holds an independent one.
	$manifestFile = Get-HookLabPayloadManifestFile $payloadFixture
	$manifestDocument = Get-Content -LiteralPath $manifestFile -Raw | ConvertFrom-Json
	$manifestDocument.payloads[0].sha256 = (Get-FileHash -LiteralPath $payloadFile -Algorithm SHA256).Hash.ToLowerInvariant()
	[IO.File]::WriteAllText($manifestFile, (($manifestDocument | ConvertTo-Json -Depth 5) + "`n"), [Text.UTF8Encoding]::new($false))
	if ((Test-HookLabPayload -HostRoot $payloadFixture) -ne $manifestDocument.payloads[0].sha256) {
		throw 'A payload manifest rewritten to match the altered bytes should verify against itself.'
	}
	Assert-PayloadFailure { Test-HookLabPayload -HostRoot $payloadFixture -ExpectedSha256 $expected } 'does not match the packaged digest' `
		'Verification accepted a payload and manifest that were rewritten together.'

	# Re-stage to undo both rewrites before the next case.
	$null = Write-HookLabPayload -BootstrapAssembly $bootstrapAssembly -HostRoot $payloadFixture

	# Truncation is a distinct failure from substitution, and the cheap check has to catch it.
	[IO.File]::WriteAllBytes($payloadFile, $original[0..1023])
	Assert-PayloadFailure { Test-HookLabPayload -HostRoot $payloadFixture } 'bytes; the manifest records' `
		'Verification accepted a truncated payload.'
	[IO.File]::WriteAllBytes($payloadFile, $original)
	$null = Test-HookLabPayload -HostRoot $payloadFixture -ExpectedSha256 $expected

	foreach ($absent in @($payloadFile, $manifestFile)) {
		$saved = [IO.File]::ReadAllBytes($absent)
		Remove-Item -LiteralPath $absent -Force
		Assert-PayloadFailure { Test-HookLabPayload -HostRoot $payloadFixture } 'nothing to deliver' `
			"Verification accepted a layout with no $(Split-Path -Leaf $absent)."
		[IO.File]::WriteAllBytes($absent, $saved)
	}
	$null = Test-HookLabPayload -HostRoot $payloadFixture -ExpectedSha256 $expected

	# No payload may be reachable from a directory dnSpy scans for extensions or probes by assembly name.
	# Both the assembly name the CLR would ask for and a renamed copy of the same bytes must be refused.
	foreach ($reachable in @(
		(Join-Path $payloadFixture 'bin\Extensions\dgSpy\HookLab.Bootstrap.dll'),
		(Join-Path $payloadFixture 'bin\HookLab.Bootstrap.dll'),
		(Join-Path $payloadFixture 'HookLab.Bootstrap.dll'),
		(Join-Path $payloadFixture 'bin\Extensions\dgSpy\innocuous-name.bin'))) {
		New-Item -ItemType Directory -Path (Split-Path -Parent $reachable) -Force | Out-Null
		Copy-Item -LiteralPath $payloadFile -Destination $reachable -Force
		Assert-PayloadFailure { Test-HookLabPayload -HostRoot $payloadFixture } 'reachable from a dnSpy extension search path' `
			"Verification accepted a payload copy at $reachable."
		Remove-Item -LiteralPath $reachable -Force
	}
	$null = Test-HookLabPayload -HostRoot $payloadFixture -ExpectedSha256 $expected

	Assert-PayloadFailure { Write-HookLabPayload -BootstrapAssembly (Join-Path $payloadFixture 'no-such-bootstrap.dll') -HostRoot $payloadFixture } `
		'HookLab bootstrap assembly not found' 'Staging accepted a missing bootstrap assembly.'
}
finally {
	if (Test-Path -LiteralPath $payloadFixture) { Remove-Item -LiteralPath $payloadFixture -Recurse -Force }
}

Write-Host 'PASSED  HookLab payload staging, digest verification, and extension-search-path exclusion'
