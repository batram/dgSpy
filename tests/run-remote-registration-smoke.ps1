param(
	[string]$HostId = 'local-remote-smoke',
	[int]$GatewayPort = 18350,
	[switch]$UseTls,
	[string]$PayloadRoot,
	[string]$KeepDeployment
)

$ErrorActionPreference = 'Stop'
$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new()
$dotnetExe=(Get-Command dotnet).Source
$powershellExe=(Get-Command powershell).Source
$repoRoot = Split-Path $PSScriptRoot -Parent
$runName = if ($UseTls) { 'remote-registration-smoke-tls' } else { 'remote-registration-smoke-plaintext' }
$runRoot = Join-Path $repoRoot "artifacts\$runName"
$seedRoot = Join-Path $runRoot 'seed'
$packageRoot = Join-Path $runRoot 'package'
$deploymentRoot = Join-Path $runRoot 'deployed'
$stateRoot = Join-Path $runRoot 'state'
$gatewayDll = Join-Path $repoRoot 'dgSpy.Gateway\bin\Release\net10.0\dgSpy.Gateway.dll'
$cliDll = Join-Path $repoRoot 'dgSpy.Cli\bin\Release\net10.0\dgspy.dll'
$targetProject = Join-Path $PSScriptRoot 'TestTargets\Milestone1Target\Milestone1Target.csproj'
$targetExe = Join-Path $PSScriptRoot 'TestTargets\Milestone1Target\bin\Debug\net48\Milestone1Target.exe'
$gatewayUrl = "http://127.0.0.1:$GatewayPort"
$mcpToken = [Guid]::NewGuid().ToString('N') + [Guid]::NewGuid().ToString('N')
$script:requestId = 0
$gatewayProcess = $null
$dnSpyPid = $null
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
	$env:DGSPY_STATE_ROOT=$stateRoot
	$env:DGSPY_PACKAGE_ROOT=$packageRoot
	$env:DGSPY_REMOTE_PAYLOAD_ROOT=$script:payloadRoot
	$env:DGSPY_GATEWAY_PATH=$gatewayDll
	Remove-Item Env:DGSPY_HOSTS_FILE,Env:DGSPY_REMOTE_ADDRESS,Env:DGSPY_REMOTE_PORT,Env:DGSPY_REMOTE_TLS_PORT,Env:DGSPY_GATEWAY_SERVER_CERTIFICATE_FILE,Env:DGSPY_GATEWAY_SERVER_CERTIFICATE_PASSWORD_FILE,Env:DGSPY_REMOTE_DISABLE_PLAINTEXT,Env:DGSPY_HOST_ID,Env:DGSPY_RPC_TOKEN,Env:DGSPY_RPC_PORT,Env:DGSPY_GATEWAY_ADDRESS,Env:DGSPY_GATEWAY_PORT,Env:DGSPY_GATEWAY_TRANSPORT,Env:DGSPY_CLIENT_CERTIFICATE_FILE,Env:DGSPY_CLIENT_CERTIFICATE_PASSWORD_FILE,Env:DGSPY_GATEWAY_CERTIFICATE_FILE -ErrorAction SilentlyContinue
	& $dotnetExe $cliDll start
	if ($LASTEXITCODE) { throw "Ordinary dgspy start failed: $LASTEXITCODE" }
	if (-not (Wait-Until { try { (Invoke-RestMethod "$gatewayUrl/health" -TimeoutSec 1).status -eq 'ok' } catch { $false } } 30)) { throw 'Gateway did not become healthy.' }
	$gatewayPid=[int](Get-Content -LiteralPath (Join-Path $stateRoot 'gateway.pid') -Raw)
	$script:gatewayProcess=Get-Process -Id $gatewayPid
}

try {
	if (Test-Path -LiteralPath $runRoot) { Remove-Item -LiteralPath $runRoot -Recurse -Force }
	New-Item -ItemType Directory -Path $seedRoot,$packageRoot,$deploymentRoot,$stateRoot -Force | Out-Null
	if ([string]::IsNullOrWhiteSpace($PayloadRoot)) { throw 'Pass -PayloadRoot from dgspy pack-host; this smoke no longer builds through a second packaging implementation.' }
	$script:payloadRoot=[IO.Path]::GetFullPath($PayloadRoot)
	Start-Gateway
	$created=Invoke-Tool 'create_remote_host_package' @{host_id=$HostId;gateway_address='127.0.0.1';use_tls=[bool]$UseTls}
	if (-not $created.gateway_ready -or $created.gateway_restart_required) { throw 'Provisioning did not activate the running Gateway.' }
	$archive=$created.archive_path
	& (Join-Path $PSScriptRoot 'verify-remote-host-package.ps1') -ArchivePath $archive
	& tar.exe -xf $archive -C $deploymentRoot
	if ($LASTEXITCODE) { throw "Package extraction failed: $LASTEXITCODE" }
	& $dotnetExe build $targetProject -c Debug --nologo -v:quiet
	if ($LASTEXITCODE) { throw "Target build failed: $LASTEXITCODE" }

	$remoteConfiguration=Get-Content -LiteralPath (Join-Path $deploymentRoot 'remote-host.json') -Raw | ConvertFrom-Json
	$env:DGSPY_HOST_ID=(Get-Content -LiteralPath (Join-Path $deploymentRoot 'state\host.id') -Raw).Trim()
	$env:DGSPY_RPC_TOKEN=(Get-Content -LiteralPath (Join-Path $deploymentRoot 'state\rpc.token') -Raw).Trim()
	$env:DGSPY_GATEWAY_ADDRESS=$remoteConfiguration.gateway_address
	$env:DGSPY_GATEWAY_PORT=[string]$remoteConfiguration.gateway_port
	$env:DGSPY_GATEWAY_TRANSPORT=$remoteConfiguration.transport
	if ($remoteConfiguration.transport -eq 'tls') { $env:DGSPY_CLIENT_CERTIFICATE_FILE=Join-Path $deploymentRoot $remoteConfiguration.client_certificate_file; $env:DGSPY_CLIENT_CERTIFICATE_PASSWORD_FILE=Join-Path $deploymentRoot $remoteConfiguration.client_certificate_password_file; $env:DGSPY_GATEWAY_CERTIFICATE_FILE=Join-Path $deploymentRoot $remoteConfiguration.gateway_certificate_file }
	$dnSpyExe=Join-Path $deploymentRoot 'dnSpy.exe'
	$hiddenDesktop='C:\Users\mjb\tools\agent-scripts\Invoke-OnHiddenDesktop.ps1'
	$dnSpyPid=[int](& $hiddenDesktop -FilePath $dnSpyExe -ArgumentList '--dgspy-no-window-activation' -WorkingDirectory $deploymentRoot -PassThru)
	if (-not (Wait-Until { try { @((Invoke-Tool 'list_hosts' @{}) | Where-Object { $_.host_id -eq $HostId -and $_.state -eq 'connected' }).Count -eq 1 } catch { $false } } 60)) { throw 'Packaged host did not register.' }
	$readiness=Invoke-Tool 'get_remote_host_readiness' @{host_id=$HostId}
	if (-not $readiness.registered -or -not $readiness.connected) { throw 'Remote readiness did not confirm the connected host.' }
	$hostInfo=Invoke-Tool 'get_host_info' @{host_id=$HostId}
	if ($hostInfo.host_id -ne $HostId -or $hostInfo.architecture -ne 'X64') { throw 'Routed host identity or architecture is wrong.' }
	$null=Invoke-Tool 'list_programs' @{host_id=$HostId}

	$launched=Invoke-Tool 'launch' @{host_id=$HostId;filename=$targetExe;engine='cordebug'}
	$sessionId=$launched.session_id
	$before=Invoke-Tool 'get_session_state' @{host_id=$HostId;session_id=$sessionId}
	& $dotnetExe $cliDll stop
	if ($LASTEXITCODE) { throw "Ordinary dgspy stop failed: $LASTEXITCODE" }
	Start-Gateway
	if (-not (Wait-Until { try { @((Invoke-Tool 'list_hosts' @{}) | Where-Object { $_.host_id -eq $HostId -and $_.state -eq 'connected' }).Count -eq 1 } catch { $false } } 60)) { throw 'Host did not reconnect after Gateway restart.' }
	$after=Invoke-Tool 'get_session_state' @{host_id=$HostId;session_id=$sessionId}
	if ($after.session_id -ne $before.session_id -or $after.last_event_id -lt $before.last_event_id) { throw 'Session identity or event cursor was not preserved.' }
	$null=Invoke-Tool 'claim_session' @{host_id=$HostId;session_id=$sessionId}
	$null=Invoke-Tool 'terminate' @{host_id=$HostId;session_id=$sessionId;expected_lifecycle_version=$after.lifecycle_version}
	$sessionId=$null
	Write-Host "PASS: one MCP provisioning call activated the $($remoteConfiguration.transport) listener, routed RPC, and preserved session/cursor across an ordinary CLI restart without listener environment variables."
	$succeeded=$true
}
finally {
	if ($sessionId) { try { $state=Invoke-Tool 'get_session_state' @{host_id=$HostId;session_id=$sessionId}; try { $null=Invoke-Tool 'claim_session' @{host_id=$HostId;session_id=$sessionId} } catch {}; $null=Invoke-Tool 'terminate' @{host_id=$HostId;session_id=$sessionId;expected_lifecycle_version=$state.lifecycle_version} } catch {} }
	if ($gatewayProcess -and -not $gatewayProcess.HasExited) { try { & $dotnetExe $cliDll stop | Out-Null } catch {} }
	if ($dnSpyPid) {
		$dnSpyProcess=Get-Process -Id $dnSpyPid -ErrorAction SilentlyContinue
		if ($dnSpyProcess) {
			Stop-Process -Id $dnSpyPid -Force -ErrorAction SilentlyContinue
			$dnSpyProcess.WaitForExit(10000) | Out-Null
		}
	}
	if (-not $succeeded) { Get-Content (Join-Path $runRoot 'gateway.err') -ErrorAction SilentlyContinue; Get-Content (Join-Path $runRoot 'gateway.out') -Tail 40 -ErrorAction SilentlyContinue }
	if ($succeeded -and -not $KeepDeployment -and (Test-Path -LiteralPath $runRoot)) { Remove-Item -LiteralPath $runRoot -Recurse -Force }
}
