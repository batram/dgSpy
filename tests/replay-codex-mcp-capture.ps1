param(
	[ValidateRange(1024,65535)][int]$Port = 18461,
	[string]$CapturePath = (Join-Path $PSScriptRoot '..\artifacts\codex-mcp-capture.jsonl')
)

$ErrorActionPreference = 'Stop'
$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new()
$repoRoot = Split-Path $PSScriptRoot -Parent
$runRoot = Join-Path $repoRoot 'artifacts\codex-mcp-replay'
$gateway = Join-Path $repoRoot 'dgSpy.Gateway\bin\Release\net10.0\dgSpy.Gateway.dll'
$dotnet = (Get-Command dotnet).Source
$gatewayProcess = $null

function Wait-UntilHealthy {
	param([string]$Url,[int]$Seconds = 15)
	$deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
	do {
		Start-Sleep -Milliseconds 200
		try { if ((Invoke-RestMethod "$Url/health" -TimeoutSec 1).status -eq 'ok') { return $true } } catch {}
	} until ([DateTime]::UtcNow -ge $deadline)
	return $false
}

function Read-ErrorResponseBody {
	param([System.Net.WebException]$Exception)
	if ($null -eq $Exception.Response) { return $null }
	$reader = [IO.StreamReader]::new($Exception.Response.GetResponseStream())
	try { return $reader.ReadToEnd() } finally { $reader.Dispose() }
}

try {
	if (Test-Path -LiteralPath $runRoot) { Remove-Item -LiteralPath $runRoot -Recurse -Force }
	New-Item -ItemType Directory -Path $runRoot | Out-Null
	[IO.File]::WriteAllText((Join-Path $runRoot 'host.token'), 'unused-host-token')
	[IO.File]::WriteAllText((Join-Path $runRoot 'hosts.json'), '{"hosts":[{"host_id":"host-a","address":"127.0.0.1","port":19999,"token_file":"host.token"}]}')

	$env:DGSPY_URL = "http://127.0.0.1:$Port"
	$env:DGSPY_TOKEN = 'codex-replay-token'
	$env:DGSPY_HOSTS_FILE = Join-Path $runRoot 'hosts.json'
	[Environment]::SetEnvironmentVariable('PATH', $null, [EnvironmentVariableTarget]::Process)
	$gatewayProcess = Start-Process -FilePath $dotnet -ArgumentList ('"' + $gateway + '"') -WorkingDirectory (Split-Path $gateway) -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $runRoot 'gateway.out') -RedirectStandardError (Join-Path $runRoot 'gateway.err')
	$baseUrl = "http://127.0.0.1:$Port"
	if (-not (Wait-UntilHealthy $baseUrl)) { throw "Gateway did not become healthy. See $runRoot." }

	$capturedRequests = Get-Content -LiteralPath $CapturePath | ForEach-Object { $_ | ConvertFrom-Json }
	$sessionId = $null
	$responses = @()
	foreach ($captured in $capturedRequests) {
		$headers = @{
			'X-dgSpy-Token' = 'codex-replay-token'
			'Accept' = 'text/event-stream, application/json'
			'User-Agent' = [string]$captured.headers.'User-Agent'
		}
		if (-not [string]::IsNullOrWhiteSpace($sessionId)) {
			$headers['Mcp-Session-Id'] = $sessionId
			$headers['MCP-Protocol-Version'] = '2025-06-18'
		}

		$statusCode = $null
		$responseBody = $null
		try {
			if ($captured.method -eq 'GET') {
				$response = Invoke-WebRequest -UseBasicParsing -Uri ($baseUrl + $captured.path) -Method Get -Headers $headers -TimeoutSec 10
			} else {
				$response = Invoke-WebRequest -UseBasicParsing -Uri ($baseUrl + $captured.path) -Method Post -ContentType 'application/json' -Headers $headers -Body ([string]$captured.body) -TimeoutSec 10
			}
			$statusCode = [int]$response.StatusCode
			$responseBody = $response.Content
			if ([string]::IsNullOrWhiteSpace($sessionId)) { $sessionId = $response.Headers['Mcp-Session-Id'] }
		} catch [System.Net.WebException] {
			$statusCode = [int]$_.Exception.Response.StatusCode
			$responseBody = Read-ErrorResponseBody $_.Exception
		}
		$responses += [pscustomobject]@{
			request_method = $captured.method
			jsonrpc_method = if ($captured.body) { ($captured.body | ConvertFrom-Json).method } else { $null }
			status_code = $statusCode
			body = $responseBody
		}
	}

	$responses | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $runRoot 'responses.json') -Encoding UTF8
	$responses | Format-Table request_method,jsonrpc_method,status_code,body -AutoSize
} finally {
	if ($null -ne $gatewayProcess -and -not $gatewayProcess.HasExited) { Stop-Process -Id $gatewayProcess.Id -Force -ErrorAction SilentlyContinue }
	Remove-Item Env:DGSPY_URL,Env:DGSPY_TOKEN,Env:DGSPY_HOSTS_FILE -ErrorAction SilentlyContinue
}
