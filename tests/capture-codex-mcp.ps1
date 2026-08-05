param(
	[ValidateRange(1024,65535)][int]$Port = 7350,
	[ValidateRange(1,20)][int]$MaxRequests = 6,
	[ValidateRange(5,300)][int]$TimeoutSeconds = 60,
	[string]$OutputPath = (Join-Path $PSScriptRoot '..\artifacts\codex-mcp-capture.jsonl')
)

$ErrorActionPreference = 'Stop'
$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new()

function Write-JsonResponse {
	param(
		[System.Net.HttpListenerContext]$Context,
		[int]$StatusCode,
		[AllowNull()][object]$Payload,
		[string]$SessionId
	)

	$Context.Response.StatusCode = $StatusCode
	if (-not [string]::IsNullOrWhiteSpace($SessionId)) {
		$Context.Response.Headers['Mcp-Session-Id'] = $SessionId
	}
	if ($null -ne $Payload) {
		$json = $Payload | ConvertTo-Json -Depth 20 -Compress
		$bytes = [Text.Encoding]::UTF8.GetBytes($json)
		$Context.Response.ContentType = 'application/json; charset=utf-8'
		$Context.Response.ContentLength64 = $bytes.Length
		$Context.Response.OutputStream.Write($bytes, 0, $bytes.Length)
	}
	$Context.Response.Close()
}

function Get-SafeHeaders {
	param([System.Net.WebHeaderCollection]$Headers)

	$safe = [ordered]@{}
	foreach ($name in $Headers.AllKeys) {
		if ($name -in @('Authorization','X-dgSpy-Token','Cookie')) {
			$safe[$name] = '<redacted>'
		} else {
			$safe[$name] = $Headers[$name]
		}
	}
	return $safe
}

$resolvedOutput = [IO.Path]::GetFullPath($OutputPath)
$outputDirectory = Split-Path $resolvedOutput -Parent
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
if (Test-Path -LiteralPath $resolvedOutput) { Remove-Item -LiteralPath $resolvedOutput -Force }

$listener = [Net.HttpListener]::new()
$listener.Prefixes.Add("http://127.0.0.1:$Port/")
$sessionId = [Guid]::NewGuid().ToString('N')
$deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
$captured = 0

try {
	$listener.Start()
	Write-Host "Capturing Codex MCP traffic at http://127.0.0.1:$Port/mcp"
	Write-Host "Output: $resolvedOutput"
	Write-Host 'Trigger an MCP access check now.'

	while ($captured -lt $MaxRequests -and [DateTime]::UtcNow -lt $deadline) {
		$pending = $listener.GetContextAsync()
		$remaining = [Math]::Max(1, [int]($deadline - [DateTime]::UtcNow).TotalMilliseconds)
		if (-not $pending.Wait($remaining)) { break }

		$context = $pending.Result
		$request = $context.Request
		$body = ''
		if ($request.HasEntityBody) {
			$reader = [IO.StreamReader]::new($request.InputStream, $request.ContentEncoding)
			try { $body = $reader.ReadToEnd() } finally { $reader.Dispose() }
		}

		$parsed = $null
		$parseError = $null
		if (-not [string]::IsNullOrWhiteSpace($body)) {
			try { $parsed = $body | ConvertFrom-Json } catch { $parseError = $_.Exception.Message }
		}
		$record = [ordered]@{
			timestamp_utc = [DateTime]::UtcNow.ToString('O')
			method = $request.HttpMethod
			path = $request.Url.AbsolutePath
			headers = Get-SafeHeaders $request.Headers
			body = $body
			parse_error = $parseError
		}
		Add-Content -LiteralPath $resolvedOutput -Value ($record | ConvertTo-Json -Depth 20 -Compress) -Encoding UTF8
		$captured++

		if ($request.HttpMethod -eq 'GET') {
			Write-JsonResponse $context 405 $null $sessionId
			continue
		}
		if ($request.Url.AbsolutePath -ne '/mcp' -or $null -eq $parsed) {
			Write-JsonResponse $context 400 @{ jsonrpc='2.0'; id=$null; error=@{ code=-32600; message='Invalid request.' } } $sessionId
			continue
		}

		$requestId = if ($parsed.PSObject.Properties.Name -contains 'id') { $parsed.id } else { $null }
		switch ([string]$parsed.method) {
			'initialize' {
				$requestedVersion = [string]$parsed.params.protocolVersion
				Write-JsonResponse $context 200 @{
					jsonrpc = '2.0'
					id = $requestId
					result = @{
						protocolVersion = $requestedVersion
						capabilities = @{ tools = @{ listChanged = $false }; resources = @{ listChanged = $false } }
						serverInfo = @{ name = 'dgSpy Codex capture'; version = '0.1.0' }
					}
				} $sessionId
			}
			'notifications/initialized' { Write-JsonResponse $context 202 $null $sessionId }
			'tools/list' { Write-JsonResponse $context 200 @{ jsonrpc='2.0'; id=$requestId; result=@{ tools=@() } } $sessionId }
			'resources/list' { Write-JsonResponse $context 200 @{ jsonrpc='2.0'; id=$requestId; result=@{ resources=@() } } $sessionId }
			'resources/templates/list' { Write-JsonResponse $context 200 @{ jsonrpc='2.0'; id=$requestId; result=@{ resourceTemplates=@() } } $sessionId }
			default { Write-JsonResponse $context 200 @{ jsonrpc='2.0'; id=$requestId; error=@{ code=-32601; message='Method not found.' } } $sessionId }
		}
	}
} finally {
	if ($listener.IsListening) { $listener.Stop() }
	$listener.Close()
}

Write-Host "Captured $captured request(s)."
Write-Host $resolvedOutput
