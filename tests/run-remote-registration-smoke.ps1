param(
	[string]$HostId = 'local-remote-smoke',
	[int]$GatewayPort = 18350,
	[int]$RemotePort = 18352,
	[switch]$UseTls,
	[string]$KeepDeployment
)

$ErrorActionPreference = 'Stop'
$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new()
$dotnetExe=(Get-Command dotnet).Source
$powershellExe=(Get-Command powershell).Source
$repoRoot = Split-Path $PSScriptRoot -Parent
$runRoot = Join-Path $repoRoot 'artifacts\remote-registration-smoke'
$packageRoot = Join-Path $runRoot 'package'
$deploymentRoot = Join-Path $runRoot 'deployed'
$gatewayDll = Join-Path $repoRoot 'dgSpy.Gateway\bin\Release\net7.0\dgSpy.Gateway.dll'
$targetProject = Join-Path $PSScriptRoot 'TestTargets\Milestone1Target\Milestone1Target.csproj'
$targetExe = Join-Path $PSScriptRoot 'TestTargets\Milestone1Target\bin\Debug\net48\Milestone1Target.exe'
$gatewayUrl = "http://127.0.0.1:$GatewayPort"
$mcpToken = [Guid]::NewGuid().ToString('N') + [Guid]::NewGuid().ToString('N')
$script:requestId = 0
$gatewayProcess = $null
$dnSpyProcess = $null
$sessionId = $null
$succeeded = $false

function Wait-Until {
	param([scriptblock]$Condition,[int]$TimeoutSeconds=30)
	$deadline=[DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
	do { Start-Sleep -Milliseconds 250; if (& $Condition) { return $true } } until([DateTime]::UtcNow -gt $deadline)
	return $false
}

function Invoke-Tool {
	param([string]$Name,[hashtable]$Arguments)
	$script:requestId++
	$body=@{jsonrpc='2.0';id=$script:requestId;method='tools/call';params=@{name=$Name;arguments=$Arguments}} | ConvertTo-Json -Depth 12
	$response=Invoke-RestMethod -Uri "$gatewayUrl/mcp" -Method Post -ContentType 'application/json' -Headers @{'X-dgSpy-Token'=$mcpToken} -Body $body -TimeoutSec 25
	if ($response.error) { throw ($response.error | ConvertTo-Json -Compress) }
	if ($response.result.isError) { throw $response.result.content[0].text }
	return $response.result.content[0].text | ConvertFrom-Json
}

function Start-Gateway {
	$env:DGSPY_URL=$gatewayUrl
	$env:DGSPY_TOKEN=$mcpToken
	$env:DGSPY_HOSTS_FILE=Join-Path $packageRoot 'gateway-hosts.json'
	$env:DGSPY_REMOTE_ADDRESS='127.0.0.1'
	$env:DGSPY_REMOTE_PORT=[string]$RemotePort
	$gatewayConfiguration=Get-Content -LiteralPath $env:DGSPY_HOSTS_FILE -Raw | ConvertFrom-Json
	if ($gatewayConfiguration.tls) { $env:DGSPY_REMOTE_TLS_PORT=[string]$gatewayConfiguration.tls.port; $env:DGSPY_GATEWAY_SERVER_CERTIFICATE_FILE=Join-Path $packageRoot $gatewayConfiguration.tls.server_certificate_file; $env:DGSPY_GATEWAY_SERVER_CERTIFICATE_PASSWORD_FILE=Join-Path $packageRoot $gatewayConfiguration.tls.server_certificate_password_file; $env:DGSPY_REMOTE_DISABLE_PLAINTEXT='true' }
	else { Remove-Item Env:DGSPY_REMOTE_TLS_PORT,Env:DGSPY_GATEWAY_SERVER_CERTIFICATE_FILE,Env:DGSPY_REMOTE_DISABLE_PLAINTEXT -ErrorAction SilentlyContinue }
	# Some automated shells inject both Path and PATH. Windows PowerShell's Start-Process rejects that
	# duplicate case-insensitive key. Tool-driven work is complete before this process-local cleanup.
	[Environment]::SetEnvironmentVariable('PATH',$null,[EnvironmentVariableTarget]::Process)
	$script:gatewayProcess=Start-Process -FilePath $dotnetExe -ArgumentList ('"'+$gatewayDll+'"') -WorkingDirectory (Split-Path $gatewayDll) -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $runRoot 'gateway.out') -RedirectStandardError (Join-Path $runRoot 'gateway.err')
	if (-not (Wait-Until { try { (Invoke-RestMethod "$gatewayUrl/health" -TimeoutSec 1).status -eq 'ok' } catch { $false } } 30)) { throw 'Gateway did not become healthy.' }
}

try {
	if (Test-Path -LiteralPath $runRoot) { Remove-Item -LiteralPath $runRoot -Recurse -Force }
	New-Item -ItemType Directory -Path $packageRoot,$deploymentRoot -Force | Out-Null
	if ($UseTls) { & (Join-Path $repoRoot 'pack-remote-host.ps1') -SkipBuild -HostId $HostId -GatewayAddress '127.0.0.1' -GatewayTlsPort $RemotePort -OutputDirectory $packageRoot -UseTls }
	else { & (Join-Path $repoRoot 'pack-remote-host.ps1') -SkipBuild -HostId $HostId -GatewayAddress '127.0.0.1' -GatewayPort $RemotePort -OutputDirectory $packageRoot }
	$archive=Join-Path $packageRoot "dgSpy-remote-host-$HostId-win-x64.zip"
	& (Join-Path $PSScriptRoot 'verify-remote-host-package.ps1') -ArchivePath $archive
	& tar.exe -xf $archive -C $deploymentRoot
	if ($LASTEXITCODE) { throw "Package extraction failed: $LASTEXITCODE" }
	& $dotnetExe build $targetProject -c Debug --nologo -v:quiet
	if ($LASTEXITCODE) { throw "Target build failed: $LASTEXITCODE" }

	Start-Gateway
	$remoteConfiguration=Get-Content -LiteralPath (Join-Path $deploymentRoot 'remote-host.json') -Raw | ConvertFrom-Json
	$env:DGSPY_HOST_ID=(Get-Content -LiteralPath (Join-Path $deploymentRoot 'state\host.id') -Raw).Trim()
	$env:DGSPY_RPC_TOKEN=(Get-Content -LiteralPath (Join-Path $deploymentRoot 'state\rpc.token') -Raw).Trim()
	$env:DGSPY_GATEWAY_ADDRESS=$remoteConfiguration.gateway_address
	$env:DGSPY_GATEWAY_PORT=[string]$remoteConfiguration.gateway_port
	$env:DGSPY_GATEWAY_TRANSPORT=$remoteConfiguration.transport
	if ($remoteConfiguration.transport -eq 'tls') { $env:DGSPY_CLIENT_CERTIFICATE_FILE=Join-Path $deploymentRoot $remoteConfiguration.client_certificate_file; $env:DGSPY_CLIENT_CERTIFICATE_PASSWORD_FILE=Join-Path $deploymentRoot $remoteConfiguration.client_certificate_password_file; $env:DGSPY_GATEWAY_CERTIFICATE_FILE=Join-Path $deploymentRoot $remoteConfiguration.gateway_certificate_file }
	$dnSpyExe=Join-Path $deploymentRoot 'dnSpy.exe'
	$dnSpyProcess=Start-Process -FilePath $dnSpyExe -ArgumentList '--dgspy-no-window-activation' -WorkingDirectory $deploymentRoot -WindowStyle Hidden -PassThru
	if (-not (Wait-Until { try { @((Invoke-Tool 'list_hosts' @{}) | Where-Object { $_.host_id -eq $HostId -and $_.state -eq 'connected' }).Count -eq 1 } catch { $false } } 60)) { throw 'Packaged host did not register.' }
	$hostInfo=Invoke-Tool 'get_host_info' @{host_id=$HostId}
	if ($hostInfo.host_id -ne $HostId -or $hostInfo.architecture -ne 'X64') { throw 'Routed host identity or architecture is wrong.' }

	$launched=Invoke-Tool 'launch' @{host_id=$HostId;filename=$targetExe;engine='cordebug'}
	$sessionId=$launched.session_id
	$before=Invoke-Tool 'get_session_state' @{host_id=$HostId;session_id=$sessionId}
	Stop-Process -Id $gatewayProcess.Id -Force
	$gatewayProcess.WaitForExit(10000) | Out-Null
	Start-Gateway
	if (-not (Wait-Until { try { @((Invoke-Tool 'list_hosts' @{}) | Where-Object { $_.host_id -eq $HostId -and $_.state -eq 'connected' }).Count -eq 1 } catch { $false } } 60)) { throw 'Host did not reconnect after Gateway restart.' }
	$after=Invoke-Tool 'get_session_state' @{host_id=$HostId;session_id=$sessionId}
	if ($after.session_id -ne $before.session_id -or $after.last_event_id -lt $before.last_event_id) { throw 'Session identity or event cursor was not preserved.' }
	$null=Invoke-Tool 'claim_session' @{host_id=$HostId;session_id=$sessionId}
	$null=Invoke-Tool 'terminate' @{host_id=$HostId;session_id=$sessionId;expected_state_version=$after.state_version}
	$sessionId=$null
	Write-Host "PASS: packaged $($remoteConfiguration.transport) host registered, routed RPC, and preserved session/cursor across Gateway restart."
	$succeeded=$true
}
finally {
	if ($sessionId) { try { $state=Invoke-Tool 'get_session_state' @{host_id=$HostId;session_id=$sessionId}; try { $null=Invoke-Tool 'claim_session' @{host_id=$HostId;session_id=$sessionId} } catch {}; $null=Invoke-Tool 'terminate' @{host_id=$HostId;session_id=$sessionId;expected_state_version=$state.state_version} } catch {} }
	if ($gatewayProcess -and -not $gatewayProcess.HasExited) { Stop-Process -Id $gatewayProcess.Id -Force -ErrorAction SilentlyContinue }
	if ($dnSpyProcess -and -not $dnSpyProcess.HasExited) { Stop-Process -Id $dnSpyProcess.Id -Force -ErrorAction SilentlyContinue; $dnSpyProcess.WaitForExit(10000) | Out-Null }
	if (-not $succeeded) { Get-Content (Join-Path $runRoot 'gateway.err') -ErrorAction SilentlyContinue; Get-Content (Join-Path $runRoot 'gateway.out') -Tail 40 -ErrorAction SilentlyContinue }
	if ($succeeded -and -not $KeepDeployment -and (Test-Path -LiteralPath $runRoot)) { Remove-Item -LiteralPath $runRoot -Recurse -Force }
}
