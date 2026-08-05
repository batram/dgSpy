param(
	[string]$HostId,
	[string]$RpcToken,
	[ValidateRange(1, 65535)][int]$Port = 7351,
	[switch]$ResetIdentity,
	[switch]$InitializeOnly
)

$ErrorActionPreference = 'Stop'
$bundleRoot = Split-Path -Parent $PSScriptRoot
$stateDirectory = Join-Path $bundleRoot 'state'
$hostIdFile = Join-Path $stateDirectory 'host.id'
$tokenFile = Join-Path $stateDirectory 'rpc.token'
$dnSpyExe = Join-Path $bundleRoot 'dnSpy.exe'
$configurationFile = Join-Path $bundleRoot 'remote-host.json'

if (-not (Test-Path -LiteralPath $dnSpyExe -PathType Leaf)) { throw "The bundle is incomplete: $dnSpyExe is missing." }
New-Item -ItemType Directory -Path $stateDirectory -Force | Out-Null
if (Test-Path -LiteralPath $configurationFile -PathType Leaf) {
	$configuration = Get-Content -LiteralPath $configurationFile -Raw | ConvertFrom-Json
	if ([string]::IsNullOrWhiteSpace($HostId)) { $HostId = $configuration.host_id }
	$env:DGSPY_GATEWAY_ADDRESS = $configuration.gateway_address
	$env:DGSPY_GATEWAY_PORT = ([int]$configuration.gateway_port).ToString([Globalization.CultureInfo]::InvariantCulture)
}
if ($ResetIdentity) { Remove-Item -LiteralPath $hostIdFile, $tokenFile -Force -ErrorAction SilentlyContinue }

function Read-OrCreateValue {
	param([string]$Path, [string]$SuppliedValue, [scriptblock]$CreateValue)
	if (-not [string]::IsNullOrWhiteSpace($SuppliedValue)) {
		$value = $SuppliedValue.Trim()
		[IO.File]::WriteAllText($Path, $value, [Text.UTF8Encoding]::new($false))
		return $value
	}
	if (Test-Path -LiteralPath $Path -PathType Leaf) {
		$value = [IO.File]::ReadAllText($Path).Trim()
		if (-not [string]::IsNullOrWhiteSpace($value)) { return $value }
	}
	$value = & $CreateValue
	[IO.File]::WriteAllText($Path, $value, [Text.UTF8Encoding]::new($false))
	return $value
}

$resolvedHostId = Read-OrCreateValue -Path $hostIdFile -SuppliedValue $HostId -CreateValue {
	"$($env:COMPUTERNAME)-$([Guid]::NewGuid().ToString('N').Substring(0, 12))".ToLowerInvariant()
}
$resolvedToken = Read-OrCreateValue -Path $tokenFile -SuppliedValue $RpcToken -CreateValue {
	$bytes = [byte[]]::new(32)
	$generator = [Security.Cryptography.RandomNumberGenerator]::Create()
	try { $generator.GetBytes($bytes) }
	finally { $generator.Dispose() }
	[Convert]::ToBase64String($bytes)
}

# These assignments affect this launcher and its child only. No machine/user environment, registry,
# service, firewall, or PATH configuration is changed.
$env:DGSPY_HOST_ID = $resolvedHostId
$env:DGSPY_RPC_TOKEN = $resolvedToken
$env:DGSPY_RPC_PORT = $Port.ToString([Globalization.CultureInfo]::InvariantCulture)
Write-Host "Starting dgSpy remote host '$resolvedHostId'; local RPC remains on loopback port $Port."
if ($env:DGSPY_GATEWAY_ADDRESS) { Write-Host "Outbound Gateway: $($env:DGSPY_GATEWAY_ADDRESS):$($env:DGSPY_GATEWAY_PORT)" }
Write-Host "Credential: $tokenFile"
if ($InitializeOnly) { return }
& $dnSpyExe --dgspy-no-window-activation
exit $LASTEXITCODE
