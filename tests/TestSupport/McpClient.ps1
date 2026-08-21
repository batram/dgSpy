# Shared MCP client for the live smoke harnesses.
#
# Extracted verbatim from run-milestone1-smoke.ps1 so a second live smoke can use it without a
# second copy. Copying it would be worse than the duplication looks: these helpers encode protocol
# ceremony and several Windows PowerShell 5.1 landmines, and two copies drift apart exactly where
# that knowledge lives.
#
# Dot-source it, then call Initialize-McpClient:
#
#     . "$PSScriptRoot\TestSupport\McpClient.ps1"
#     Initialize-McpClient -GatewayUrl $gatewayUrl -Token $token
#
# Dot-sourcing runs this in the caller's scope, so the $script: state below belongs to the calling
# smoke. That is deliberate: each smoke owns its own counters and its own failure list.
#
# ASCII-only by policy: an em dash in a .ps1 parses as a stray quote under CP1252.

function Initialize-McpClient {
	<#
	.SYNOPSIS
		Point the client at a gateway and reset per-run state.
	#>
	param(
		[Parameter(Mandatory)][string]$GatewayUrl,
		[Parameter(Mandatory)][string]$Token
	)
	$script:mcpGatewayUrl = $GatewayUrl
	$script:mcpToken = $Token
	$script:requestId = 0
	$script:failures = @()
	$script:checks = 0
	$script:section = 'startup'
	$script:lastCall = ''
	$script:toolDefinitions = $null
}

function Assert-That {
	# $Condition stays untyped: -match/-like against a collection yield the matches rather than a
	# boolean, and a [bool] parameter would throw instead of failing the check.
	param([string]$What, $Condition, [string]$Detail = '')
	$script:checks++
	if (@($Condition).Count -gt 0 -and [bool](@($Condition) | Select-Object -Last 1)) { Write-Host "  PASS  $What" -ForegroundColor DarkGreen }
	else {
		# Most call sites pass no detail, and "FAIL <sentence>" alone leaves a CI log with nothing to
		# work from. The section and the last tool call are always known, so always say them.
		$context = "[$script:section]" + $(if ($script:lastCall) { " after $script:lastCall" })
		Write-Host "  FAIL  $What $Detail" -ForegroundColor Red
		Write-Host "        $context" -ForegroundColor DarkRed
		$script:failures += "$What $Detail $context"
	}
}

# Sections are already announced for a human reading along; recording them makes every later failure
# and every tool error carry where it happened without touching the call sites.
function Write-Section {
	param([string]$Name)
	$script:section = $Name
	Write-Host "== $Name ==" -ForegroundColor Cyan
}

function Invoke-Mcp {
	param([string]$Method, [hashtable]$Parameters, [hashtable]$Headers, [switch]$Raw, [int]$TimeoutSeconds = 25)
	$script:requestId++
	$body = @{ jsonrpc = '2.0'; id = $script:requestId; method = $Method }
	if ($null -ne $Parameters) { $body.params = $Parameters }
	if ($null -eq $Headers) { $Headers = @{ 'X-dgSpy-Token' = $script:mcpToken } }
	$response = Invoke-RestMethod -Uri ($script:mcpGatewayUrl + '/mcp') -Method Post -ContentType 'application/json' `
		-Headers $Headers -TimeoutSec $TimeoutSeconds -Body ($body | ConvertTo-Json -Depth 12)
	if ($Raw) { return $response }
	if ($null -ne $response.error) { throw ('MCP error: ' + ($response.error | ConvertTo-Json -Compress)) }
	return $response.result
}

# Tools retain a JSON text fallback for compatibility. Protocol-shape checks separately verify
# that structuredContent is an object, as required by MCP, including when the tool payload is an array.
function Invoke-Tool {
	# -AsText returns the raw JSON. Prefer it for emptiness checks: ConvertFrom-Json collapses an
	# empty array in ways that make .Count unreliable in Windows PowerShell.
	# -TimeoutSeconds raises the HTTP wait for one call. The 25 s default suits ordinary tools and is far
	# too short for a few: initialize_hooklab alone has a 20 s completion deadline of its own before any
	# staging or payload load, so on a real Unity player it timed out client-side while the operation was
	# still running - which reads as a product hang and is not one.
	param([string]$Name, [hashtable]$Arguments, [switch]$ExpectError, [switch]$AsText, [int]$TimeoutSeconds = 25)
	# The arguments are what makes a tool error actionable: "get_frame failed: thread_id is required"
	# reads as a product bug until you can see the harness passed thread_id as null.
	$rendered = "$Name($($Arguments | ConvertTo-Json -Compress -Depth 6))"
	$script:lastCall = $rendered
	$result = Invoke-Mcp -Method 'tools/call' -Parameters @{ name = $Name; arguments = $Arguments } -TimeoutSeconds $TimeoutSeconds
	if ($ExpectError) {
		if (-not $result.isError) { throw "Tool $rendered was expected to fail but succeeded, returning: $($result.content[0].text)" }
		return $result.content[0].text
	}
	if ($result.isError) { throw ("Tool $rendered failed: " + $result.content[0].text) }
	if ($AsText) { return $result.content[0].text }
	# Windows PowerShell 5.1's ConvertFrom-Json hands a JSON array to the pipeline as one object
	# instead of enumerating it, so @(Invoke-Tool ...)[0] would return the whole array rather than
	# its first entry. Member enumeration hides that for property reads but not for reflection over
	# PSObject.Properties. Write-Output enumerates, so callers index and count real entries.
	Write-Output ($result.content[0].text | ConvertFrom-Json)
}

# Mutations require an exact scoped version from a current state read. Keep that protocol ceremony
# visible in the live harness without duplicating it at every call site. Tests that intentionally
# exercise missing or stale guards must continue to call Invoke-Tool directly.
function Invoke-MutatingTool {
	param([string]$Name, [hashtable]$Arguments, [switch]$ExpectError, [switch]$AsText, [int]$TimeoutSeconds = 25)
	$callArguments = @{} + $Arguments
	if (-not $callArguments.ContainsKey('session_id')) {
		if ([string]::IsNullOrWhiteSpace($script:activeSessionId)) { throw "Mutation $Name has no session_id and no active smoke session." }
		$callArguments.session_id = $script:activeSessionId
	}
	$state = Invoke-Tool -Name 'get_session_state' -Arguments @{ session_id = $callArguments.session_id }
	if ($null -eq $script:toolDefinitions) { $script:toolDefinitions = @((Invoke-Mcp -Method 'tools/list' -Parameters @{}).tools) }
	$definition = @($script:toolDefinitions | Where-Object { $_.name -eq $Name })[0]
	$required = @($definition.inputSchema.required)
	$guard = @($required | Where-Object { $_ -match '^expected_.+_version$' })[0]
	if ([string]::IsNullOrWhiteSpace($guard)) { throw "Mutation $Name advertises no scoped version guard." }
	$stateProperty = $guard.Substring('expected_'.Length)
	if (-not $callArguments.ContainsKey($guard)) { $callArguments[$guard] = $state.$stateProperty }
	if ($required -contains 'expected_stop_id' -and -not $callArguments.ContainsKey('expected_stop_id')) { $callArguments.expected_stop_id = $state.stop_id }
	return Invoke-Tool -Name $Name -Arguments $callArguments -ExpectError:$ExpectError -AsText:$AsText -TimeoutSeconds $TimeoutSeconds
}

function Wait-Until {
	param([scriptblock]$Condition, [int]$TimeoutSeconds = 30)
	$deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
	do {
		Start-Sleep -Milliseconds 250
		if (& $Condition) { return $true }
	} until ([DateTime]::UtcNow -gt $deadline)
	return $false
}
