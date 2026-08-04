$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new()

function Invoke-DgSpyRpc {
	param(
		[Parameter(Mandatory=$true)][string]$OperationName,
		[hashtable]$OperationArguments = @{},
		[int]$RpcPort = 7351,
		[int]$DeadlineSeconds = 15,
		[string]$RpcToken,
		[string]$HostId
	)
	if ([string]::IsNullOrWhiteSpace($RpcToken)) { $RpcToken = $env:DGSPY_RPC_TOKEN }
	if ([string]::IsNullOrWhiteSpace($RpcToken)) {
		$tokenPath = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'dgSpy\rpc.token'
		if (-not (Test-Path -LiteralPath $tokenPath)) { throw "No extension RPC credential exists at '$tokenPath'." }
		$RpcToken = (Get-Content -LiteralPath $tokenPath -Raw).Trim()
	}
	if ([string]::IsNullOrWhiteSpace($RpcToken)) { throw 'The extension RPC credential is empty.' }
	if ([string]::IsNullOrWhiteSpace($HostId)) { $HostId = $env:DGSPY_HOST_ID }

	$client = [Net.Sockets.TcpClient]::new()
	$client.Connect('127.0.0.1', $RpcPort)
	try {
		$stream = $client.GetStream()
		$reader = [IO.StreamReader]::new($stream, [Text.Encoding]::UTF8, $false, 4096, $true)
		$writer = [IO.StreamWriter]::new($stream, [Text.UTF8Encoding]::new($false), 4096, $true)
		$writer.AutoFlush = $true

		$writer.WriteLine((@{
			version = 2
			request_id = [Guid]::NewGuid().ToString('N')
			host_id = $HostId
			authentication_token = $RpcToken
			operation = 'ping'
			arguments = @{}
			deadline_utc = [DateTime]::UtcNow.AddSeconds(3)
			} | ConvertTo-Json -Compress))
		$pingResponse = $reader.ReadLine() | ConvertFrom-Json
		if ($pingResponse.error) { throw "ping failed: $($pingResponse.error.code): $($pingResponse.error.message)" }
		$actualHostId = $pingResponse.result.host_id
		if ([string]::IsNullOrWhiteSpace($actualHostId)) { throw 'ping did not return host_id.' }
		if (-not [string]::IsNullOrWhiteSpace($HostId) -and $actualHostId -ne $HostId) {
			throw "Configured host '$HostId' does not match extension host '$actualHostId'."
		}

		$writer.WriteLine((@{
			version = 2
			request_id = [Guid]::NewGuid().ToString('N')
			host_id = $actualHostId
			authentication_token = $RpcToken
			operation = $OperationName
			arguments = $OperationArguments
			deadline_utc = [DateTime]::UtcNow.AddSeconds($DeadlineSeconds)
			} | ConvertTo-Json -Compress -Depth 8))
		$response = $reader.ReadLine() | ConvertFrom-Json
		if ($response.error) { throw "$OperationName failed: $($response.error.code): $($response.error.message)" }
		$response.result
	} finally {
		$client.Dispose()
	}
}
