param()

$ErrorActionPreference = 'Stop'
$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new()
. (Join-Path $PSScriptRoot 'TestSupport\Invoke-DgSpyRpc.ps1')

$script:checks = 0
$script:failures = @()
function Assert-That {
	param([string]$What, $Condition, [string]$Detail = '')
	$script:checks++
	if (@($Condition).Count -gt 0 -and [bool](@($Condition) | Select-Object -Last 1)) {
		Write-Host "  PASS  $What" -ForegroundColor DarkGreen
	}
	else {
		Write-Host "  FAIL  $What $Detail" -ForegroundColor Red
		$script:failures += "$What $Detail"
	}
}

$sessionId = $null
try {
	$attached = Invoke-DgSpyRpc -OperationName 'attach_endpoint' -OperationArguments @{
		address = '127.0.0.1'; port = 55555; engine = 'unity'; process_is_suspended = $false; connection_timeout_ms = 10000
	} -DeadlineSeconds 30
	$sessionId = $attached.session_id
	Assert-That 'attach_endpoint reaches UCH' ($attached.state -in @('running','paused'))

	$modules = @(Invoke-DgSpyRpc -OperationName 'list_modules' -OperationArguments @{ session_id = $sessionId } | ForEach-Object { $_ })
	$plugin = $modules | Where-Object { $_.filename -like '*UltimateGlorpExplorer*' } | Select-Object -First 1
	Assert-That 'the file-backed UGE module is present' ($null -ne $plugin -and $plugin.can_set_breakpoint -and -not [string]::IsNullOrWhiteSpace($plugin.filename))
	Assert-That 'the selected module retains Unity engine identity' ($plugin.runtime_guid -eq 'ce8a11ee-73ef-4a51-b5d0-bda2e665a2b4') "(was '$($plugin.runtime_guid)')"

	$search = Invoke-DgSpyRpc -OperationName 'search_symbols' -OperationArguments @{
		session_id = $sessionId; pattern = 'Update'; module = $plugin.filename; kinds = @('method'); count = 40
	} -DeadlineSeconds 60
	$method = @($search.symbols | Where-Object { $_.method_token -gt 0 }) | Select-Object -First 1
	Assert-That 'bounded symbol search returns a method token' ($null -ne $method)

	$metadata = Invoke-DgSpyRpc -OperationName 'get_metadata' -OperationArguments @{
		session_id = $sessionId; module = $plugin.filename; token = $method.method_token
	} -DeadlineSeconds 40
	Assert-That 'metadata resolves the selected method' ($metadata.token -eq $method.method_token)

	$raw = Invoke-DgSpyRpc -OperationName 'get_raw_module' -OperationArguments @{
		session_id = $sessionId; module = $plugin.filename; offset = 0; count = 64
	} -DeadlineSeconds 40
	$rawBytes = [Convert]::FromBase64String($raw.data_base64)
	$fileHash = (Get-FileHash -LiteralPath $plugin.filename -Algorithm SHA256).Hash.ToLowerInvariant()
	Assert-That 'raw-module paging returns a bounded PE prefix' ($rawBytes.Length -eq 64 -and $rawBytes[0] -eq 0x4D -and $rawBytes[1] -eq 0x5A -and $raw.truncated)
	Assert-That 'raw-module SHA-256 covers the complete Unity image' ($raw.sha256 -eq $fileHash) "(rpc=$($raw.sha256) file=$fileHash)"
	$rawPage = Invoke-DgSpyRpc -OperationName 'get_raw_module' -OperationArguments @{
		session_id = $sessionId; module = $plugin.filename; offset = 64; count = 32
	} -DeadlineSeconds 40
	Assert-That 'raw-module paging preserves total size and hash' ($rawPage.offset -eq 64 -and $rawPage.total_size -eq $raw.total_size -and $rawPage.sha256 -eq $raw.sha256 -and $rawPage.truncated)

	$il = Invoke-DgSpyRpc -OperationName 'get_il' -OperationArguments @{
		session_id = $sessionId; module = $plugin.filename; method_token = $method.method_token
	} -DeadlineSeconds 40
	Assert-That 'IL disassembly returns instructions' (@($il.instructions).Count -gt 0)

	$csharp = Invoke-DgSpyRpc -OperationName 'get_csharp' -OperationArguments @{
		session_id = $sessionId; module = $plugin.filename; method_token = $method.method_token
	} -DeadlineSeconds 60
	Assert-That 'C# decompilation returns the selected method' (-not [string]::IsNullOrWhiteSpace($csharp.code))

	$text = Invoke-DgSpyRpc -OperationName 'search_text' -OperationArguments @{
		session_id = $sessionId; pattern = $method.name; module = $plugin.filename; count = 20; max_methods = 200
	} -DeadlineSeconds 140
	Assert-That 'bounded decompiled-text search completes' ($text.scanned_methods -le 200)

	$fileless = @($modules | Where-Object { $_.is_dynamic -or $_.is_in_memory }) | Select-Object -First 1
	if ($fileless) {
		$filelessRaw = Invoke-DgSpyRpc -OperationName 'get_raw_module' -OperationArguments @{
			session_id = $sessionId; module = $fileless.name; offset = 0; count = 64
		} -DeadlineSeconds 40
		$filelessBytes = [Convert]::FromBase64String($filelessRaw.data_base64)
		Assert-That 'a file-less Unity module retains in-memory/dynamic identity' (($fileless.is_dynamic -or $fileless.is_in_memory) -and -not [IO.Path]::IsPathRooted($fileless.filename))
		Assert-That 'a file-less Unity raw module is serialized and hashed' ($filelessBytes.Length -eq 64 -and $filelessBytes[0] -eq 0x4D -and $filelessBytes[1] -eq 0x5A -and $filelessRaw.sha256 -match '^[0-9a-f]{64}$')
	}
	else {
		Write-Host '  OBSERVE  UCH exposed no file-less Mono module in this run' -ForegroundColor DarkYellow
	}
}
finally {
	if ($sessionId) {
		try {
			$state = Invoke-DgSpyRpc -OperationName 'get_session_state' -OperationArguments @{ session_id = $sessionId }
			if ($state.state -eq 'paused') {
				Invoke-DgSpyRpc -OperationName 'continue' -OperationArguments @{ session_id = $sessionId } -DeadlineSeconds 30 | Out-Null
			}
			$detached = Invoke-DgSpyRpc -OperationName 'detach' -OperationArguments @{ session_id = $sessionId } -DeadlineSeconds 30
			Assert-That 'safe detach leaves UCH running' ($detached.detached -and -not $detached.terminated)
		}
		catch { $script:failures += "detach failed: $($_.Exception.Message)" }
	}
}

Assert-That 'UCH remains alive' (@(Get-Process UltimateChickenHorse -ErrorAction SilentlyContinue).Count -gt 0)
if ($script:failures.Count) {
	Write-Host "FAILED  $($script:failures.Count) of $($script:checks) checks" -ForegroundColor Red
	$script:failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
	exit 1
}
Write-Host "PASSED  $($script:checks) checks" -ForegroundColor Green
