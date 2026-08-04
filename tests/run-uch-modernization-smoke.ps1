param()

$ErrorActionPreference = 'Stop'
$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new()
. (Join-Path (Split-Path $PSScriptRoot) 'ps_scratch\Invoke-DgSpyRpc.ps1')

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

	$search = Invoke-DgSpyRpc -OperationName 'search_symbols' -OperationArguments @{
		session_id = $sessionId; pattern = 'Update'; module = $plugin.filename; kinds = @('method'); count = 40
	} -DeadlineSeconds 60
	$method = @($search.symbols | Where-Object { $_.method_token -gt 0 }) | Select-Object -First 1
	Assert-That 'bounded symbol search returns a method token' ($null -ne $method)

	$metadata = Invoke-DgSpyRpc -OperationName 'get_metadata' -OperationArguments @{
		session_id = $sessionId; module = $plugin.filename; token = $method.method_token
	} -DeadlineSeconds 40
	Assert-That 'metadata resolves the selected method' ($metadata.token -eq $method.method_token)

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
