param([string]$ArchivePath = "$PSScriptRoot\..\artifacts\remote-host\dgSpy-remote-host-win-x64.zip")

$ErrorActionPreference = 'Stop'
$ArchivePath = [IO.Path]::GetFullPath($ArchivePath)
if (-not (Test-Path -LiteralPath $ArchivePath -PathType Leaf)) { throw "Archive not found: $ArchivePath" }
$extractRoot = Join-Path ([IO.Path]::GetTempPath()) "dgspy-remote-package-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $extractRoot | Out-Null
try {
	& tar.exe -xf $ArchivePath -C $extractRoot
	if ($LASTEXITCODE) { throw "Archive extraction failed with exit code $LASTEXITCODE." }
	$manifestPath = Join-Path $extractRoot 'manifest.json'
	$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
	if (-not $manifest.self_contained -or $manifest.runtime_identifier -ne 'win-x64') { throw 'Manifest does not describe a self-contained win-x64 host.' }
	foreach ($entry in $manifest.files) {
		$file = Join-Path $extractRoot ($entry.path.Replace('/', '\'))
		if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Missing manifest file: $($entry.path)" }
		$actualHash = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
		if ($actualHash -ne $entry.sha256 -or (Get-Item -LiteralPath $file).Length -ne $entry.size) { throw "Manifest mismatch: $($entry.path)" }
	}
	$launcher = Join-Path $extractRoot 'launcher\Start-dgSpyRemoteHost.ps1'
	& $launcher -InitializeOnly
	$hostId = Get-Content -LiteralPath (Join-Path $extractRoot 'state\host.id') -Raw
	$token = Get-Content -LiteralPath (Join-Path $extractRoot 'state\rpc.token') -Raw
	& $launcher -InitializeOnly
	if ($hostId -ne (Get-Content -LiteralPath (Join-Path $extractRoot 'state\host.id') -Raw)) { throw 'Host identity was not persistent.' }
	if ($token -ne (Get-Content -LiteralPath (Join-Path $extractRoot 'state\rpc.token') -Raw)) { throw 'RPC token was not persistent.' }
	Write-Host "Verified $($manifest.files.Count) packaged files and persistent launcher identity."
}
finally {
	if (Test-Path -LiteralPath $extractRoot) { Remove-Item -LiteralPath $extractRoot -Recurse -Force }
}
