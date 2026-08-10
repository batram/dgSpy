# HookLab payload staging and verification, shared by pack-dgspy.ps1, pack-remote-host.ps1,
# install-dgspy.ps1 and build-dgspy.ps1 so there is exactly one layout rule and one verifier.
#
# The payload is ONE file. HookLab.Bootstrap.dll already embeds the probe and HookLab.Contracts as
# hash-verified resources, so nothing here assembles a bundle and nothing stages into a writable cache:
# see the HookLab payload section of docs/DGSPY_BASELINE.md for why that decision was made here.
#
# Two rules this file exists to keep:
#  1. The payload never lands anywhere dnSpy scans. dnSpy loads *.x.dll from AppDirectories.BinDirectory,
#     bin\Extensions and its immediate children, and adds each of those directories to the .NET assembly
#     loader search path; the host root is app-base probed. A file named HookLab.Bootstrap.dll in any of
#     them could be bound by name into the dnSpy process, which is exactly the disk provenance the
#     bootstrap's resolver refuses. The payload therefore lives in <host root>\hooklab under a name no
#     assembly probe can ever ask for.
#  2. A recorded digest is not a verified digest. Every consumer recomputes SHA-256 over the bytes it is
#     about to use and compares against the manifest, and the installer additionally compares against the
#     independent copy in the package manifest so that editing one record is not enough.

$script:HookLabPayloadDirectoryName = 'hooklab'
$script:HookLabPayloadFileName = 'hooklab-bootstrap.net48.payload'
$script:HookLabPayloadManifestName = 'hooklab-payload-manifest.json'
# The name the CLR would probe for if a copy ever escaped into a scanned directory.
$script:HookLabPayloadAssemblyFileName = 'HookLab.Bootstrap.dll'

function Get-HookLabPayloadDirectory([string]$HostRoot) {
	return (Join-Path $HostRoot $script:HookLabPayloadDirectoryName)
}

function Get-HookLabPayloadFile([string]$HostRoot) {
	return (Join-Path (Get-HookLabPayloadDirectory $HostRoot) $script:HookLabPayloadFileName)
}

function Get-HookLabPayloadManifestFile([string]$HostRoot) {
	return (Join-Path (Get-HookLabPayloadDirectory $HostRoot) $script:HookLabPayloadManifestName)
}

function Get-FileSha256([string]$Path) {
	return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

# The directories dnSpy scans for extensions, plus the app base it probes. Ordered outermost first so a
# failure message names the most surprising location first.
function Get-DgSpyScanDirectory([string]$HostRoot) {
	$directories = New-Object 'System.Collections.Generic.List[string]'
	$directories.Add($HostRoot)
	$bin = Join-Path $HostRoot 'bin'
	if (Test-Path -LiteralPath $bin -PathType Container) {
		$directories.Add($bin)
		$extensions = Join-Path $bin 'Extensions'
		if (Test-Path -LiteralPath $extensions -PathType Container) {
			$directories.Add($extensions)
			foreach ($child in Get-ChildItem -LiteralPath $extensions -Directory) { $directories.Add($child.FullName) }
		}
	}
	return $directories.ToArray()
}

# Stages the single payload file and its manifest under $HostRoot\hooklab and returns the digest.
function Write-HookLabPayload {
	param(
		[Parameter(Mandatory=$true)][string]$BootstrapAssembly,
		[Parameter(Mandatory=$true)][string]$HostRoot
	)
	if (-not (Test-Path -LiteralPath $BootstrapAssembly -PathType Leaf)) {
		throw "HookLab bootstrap assembly not found: $BootstrapAssembly. Build HookLab\HookLab.Bootstrap\HookLab.Bootstrap.csproj first."
	}
	$payloadDirectory = Get-HookLabPayloadDirectory $HostRoot
	New-Item -ItemType Directory -Path $payloadDirectory -Force | Out-Null
	$payloadFile = Get-HookLabPayloadFile $HostRoot
	Copy-Item -LiteralPath $BootstrapAssembly -Destination $payloadFile -Force
	$item = Get-Item -LiteralPath $payloadFile
	$digest = Get-FileSha256 $payloadFile
	$version = $item.VersionInfo.FileVersion
	if ([string]::IsNullOrWhiteSpace($version)) { $version = $null }
	$manifest = [ordered]@{
		format_version = 1
		payloads = @(
			[ordered]@{
				id = 'hooklab_bootstrap'
				file = $script:HookLabPayloadFileName
				assembly_name = 'HookLab.Bootstrap'
				assembly_file_version = $version
				target_framework = 'net48'
				architecture = 'x64'
				# The bootstrap carries the probe and HookLab.Contracts inside itself and verifies their
				# digests on load, so this is the only file the payload action needs.
				delivery = 'bytes_only'
				size = [int64]$item.Length
				sha256 = $digest
			}
		)
	}
	[IO.File]::WriteAllText((Get-HookLabPayloadManifestFile $HostRoot),(($manifest | ConvertTo-Json -Depth 5) + "`n"),[Text.UTF8Encoding]::new($false))
	Test-HookLabPayload -HostRoot $HostRoot -ExpectedSha256 $digest | Out-Null
	return $digest
}

# Verifies the staged payload the way a consumer must: recompute the digest over the bytes, compare it
# against the manifest, optionally against an independent record, and prove no copy is reachable from a
# directory dnSpy scans. Returns the verified digest; throws on any failure.
function Test-HookLabPayload {
	param(
		[Parameter(Mandatory=$true)][string]$HostRoot,
		[string]$ExpectedSha256
	)
	$resolvedRoot = [IO.Path]::GetFullPath($HostRoot)
	$payloadFile = Get-HookLabPayloadFile $resolvedRoot
	$manifestFile = Get-HookLabPayloadManifestFile $resolvedRoot
	if (-not (Test-Path -LiteralPath $manifestFile -PathType Leaf)) {
		throw "The host layout carries no HookLab payload manifest: $manifestFile. The payload action has nothing to deliver."
	}
	if (-not (Test-Path -LiteralPath $payloadFile -PathType Leaf)) {
		throw "The host layout carries no HookLab payload: $payloadFile. The payload action has nothing to deliver."
	}
	$manifest = Get-Content -LiteralPath $manifestFile -Raw | ConvertFrom-Json
	$entry = @($manifest.payloads | Where-Object { $_.id -eq 'hooklab_bootstrap' })
	if ($entry.Count -ne 1) { throw "The HookLab payload manifest has no single hooklab_bootstrap entry: $manifestFile" }
	$entry = $entry[0]
	if ($entry.file -ne $script:HookLabPayloadFileName) {
		throw "The HookLab payload manifest names an unexpected file '$($entry.file)'; this layout stages '$($script:HookLabPayloadFileName)'."
	}
	$item = Get-Item -LiteralPath $payloadFile
	if ([int64]$entry.size -ne [int64]$item.Length) {
		throw "The staged HookLab payload is $($item.Length) bytes; the manifest records $($entry.size). The payload is truncated or was modified."
	}
	$actual = Get-FileSha256 $payloadFile
	if ($actual -ne ([string]$entry.sha256).ToLowerInvariant()) {
		throw "The staged HookLab payload does not match its manifest digest (staged $actual, recorded $($entry.sha256)). It is incomplete or was modified."
	}
	if ($PSBoundParameters.ContainsKey('ExpectedSha256') -and -not [string]::IsNullOrWhiteSpace($ExpectedSha256)) {
		if ($actual -ne $ExpectedSha256.ToLowerInvariant()) {
			throw "The staged HookLab payload does not match the packaged digest (staged $actual, packaged $($ExpectedSha256.ToLowerInvariant())). The payload and its manifest were replaced together, or the copy is from another package."
		}
	}
	Assert-HookLabPayloadUnreachable -HostRoot $resolvedRoot -PayloadSha256 $actual -PayloadLength ([int64]$item.Length)
	return $actual
}

# The rule the bootstrap's resolver depends on: no copy of the payload may sit where dnSpy scans for
# extensions or where the CLR probes by assembly name.
function Assert-HookLabPayloadUnreachable {
	param(
		[Parameter(Mandatory=$true)][string]$HostRoot,
		[Parameter(Mandatory=$true)][string]$PayloadSha256,
		[Parameter(Mandatory=$true)][int64]$PayloadLength
	)
	foreach ($directory in Get-DgSpyScanDirectory $HostRoot) {
		foreach ($file in Get-ChildItem -LiteralPath $directory -File -ErrorAction SilentlyContinue) {
			if ($file.Name -eq $script:HookLabPayloadAssemblyFileName) {
				throw "A HookLab payload assembly is reachable from a dnSpy extension search path: $($file.FullName). Delivered-as-bytes is the only supported provenance."
			}
			# Any renamed copy is caught by content. Length first so this stays a metadata scan for the
			# thousand-odd files of a self-contained publish.
			if ($file.Length -eq $PayloadLength -and (Get-FileSha256 $file.FullName) -eq $PayloadSha256) {
				throw "A copy of the HookLab payload is reachable from a dnSpy extension search path: $($file.FullName). Delivered-as-bytes is the only supported provenance."
			}
		}
	}
}
