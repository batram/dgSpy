<#
.SYNOPSIS
	Live smoke for resumable bounded scans, module listing pages, and the one module-name rule.

.DESCRIPTION
	Three defects, all measured against a live Mono/Unity player and none of them reproducible against
	strings alone:

	  1. search_text and analyze_symbol bounded their work and offered no way to continue. That is not a
	     partial answer, it is an unreachable region. An agent swept a plugin for hotkey definitions,
	     hit max_methods, reported the sweep complete, and the user's own UI then showed three keybinds
	     it had never reached. analyze_symbol was worse: max_methods capped at 5000 against a
	     7227-method module, so the callers of a method in it could not be found at any setting.
	  2. list_modules and list_documents took no argument but session_id. Against a Unity player
	     list_modules returned 60,131 characters and blew the caller's token budget outright --- and
	     module_not_found's advice was to go and run it.
	  3. Half the family resolved `module` by equality and half by substring, so
	     get_csharp(module: "Assembly-CSharp") answered module_not_found for a module
	     search(module: "Assembly-CSharp") was happily searching.

	The unit tests in tests\dgSpy.Extension.Tests cover ModuleNameMatch and ScanCursor against strings.
	What they cannot cover is the thing that broke: a real dnlib walk over a real module, where the
	traversal order the cursor depends on comes from GetTypes() over live metadata rather than from a
	list literal. The paging checks here assert the property that matters --- paged coverage equals
	unpaged coverage, with no gap and no repeat.

	Any Mono/Unity player with an Assembly-CSharp works; the bounds are set small enough that even a
	tiny one has to page. Against Ultimate Chicken Horse (170 modules, a 20311-method Assembly-CSharp)
	the same script exercises the sizes the defects were reported at:

	    .\tests\run-scan-cursor-smoke.ps1 -AgentPort 55555 -AgentSuspended $false -TargetFramework net48

	This script attaches to an already-listening agent. Launching the player is the caller's job, for
	the reasons run-mono-target-smoke gives: Unity silently ignores a malformed --debugger-agent, and a
	server=y agent accepts exactly ONE connection per launch, so readiness must be observed rather than
	connected to. Never TCP-probe the port.

	ASCII-only by policy: an em dash in a .ps1 parses as a stray quote under CP1252.

.EXAMPLE
	.\tests\run-scan-cursor-smoke.ps1

.EXAMPLE
	.\tests\run-scan-cursor-smoke.ps1 -AgentPort 55555 -AgentSuspended $false
#>
[CmdletBinding()]
param(
	[string]$AgentAddress = '127.0.0.1',
	# 56000, deliberately not the real game's 55555, so a live UCH can never be mistaken for this.
	[int]$AgentPort = 56000,
	[bool]$AgentSuspended = $true,
	[ValidateSet('net10.0-windows','net48')][string]$TargetFramework = 'net10.0-windows',
	# Distinct from every other smoke's ports so two can run without colliding.
	[int]$GatewayPort = 17364,
	[int]$RpcPort = 7365
)

$ErrorActionPreference = 'Stop'
$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new()
$repoRoot = Split-Path $PSScriptRoot -Parent
Push-Location $repoRoot

$gatewayUrl = "http://127.0.0.1:$GatewayPort"
# Windows PowerShell 5.1 is .NET Framework: no RandomNumberGenerator.GetBytes(int).
$token = [Guid]::NewGuid().ToString('N') + [Guid]::NewGuid().ToString('N')
$dnSpyHostId = $null; $gatewayProcess = $null
$script:activeSessionId = ''

. "$PSScriptRoot\TestSupport\McpClient.ps1"
Initialize-McpClient -GatewayUrl $gatewayUrl -Token $token

$runDirectory = Join-Path $env:TEMP ("dgspy-scan-cursor-smoke-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $runDirectory -Force | Out-Null

# Pages a resumable tool to exhaustion and returns every hit it produced plus how many calls it took.
# Guarded rather than unbounded: a cursor that stops advancing would otherwise hang the suite instead
# of failing it, and that is exactly the bug class this script exists to catch.
function Invoke-Paged {
	param([string]$Name, [hashtable]$Arguments, [string]$HitsProperty, [int]$MaxCalls = 200)
	$collected = @(); $offset = 0; $calls = 0; $reached = 0
	while ($true) {
		$page = Invoke-Tool -Name $Name -Arguments ($Arguments + @{ scan_offset = $offset })
		$calls++
		$collected += @($page.$HitsProperty)
		$reached = $page.next_scan_offset
		if (-not $page.scan_truncated) { break }
		if ($page.next_scan_offset -le $offset) { throw "$Name cursor did not advance past $($page.next_scan_offset)" }
		$offset = $page.next_scan_offset
		if ($calls -ge $MaxCalls) { throw "$Name did not finish within $MaxCalls pages (offset $offset)" }
	}
	[pscustomobject]@{ Items = $collected; Calls = $calls; Reached = $reached }
}

try {
	Write-Section 'preflight'
	# Observe the agent, never connect to it. A connect-close without completing the DWP handshake
	# wedges the agent, and a bare connect while the runtime is still suspended awaiting its first
	# debugger kills the player outright. Both present as debugger bugs.
	$listening = @(Get-NetTCPConnection -State Listen -LocalPort $AgentPort -ErrorAction SilentlyContinue).Count -gt 0
	if (-not $listening) {
		throw "No listener on $AgentAddress`:$AgentPort. Launch the target first."
	}
	Assert-That "the Mono agent is listening on $AgentPort" $listening

	$gatewayDll = Join-Path $repoRoot 'dgSpy.Gateway\bin\Release\net10.0\dgSpy.Gateway.dll'
	if (-not (Test-Path $gatewayDll)) { throw "Gateway not built at $gatewayDll. Run .\build-dgspy.ps1." }

	$env:DGSPY_URL = $gatewayUrl
	$env:DGSPY_TOKEN = $token
	$dnSpyHostId = & "$PSScriptRoot\TestSupport\Start-DgSpyHost.ps1" -RpcPort $RpcPort -TargetFramework $TargetFramework
	$gatewayProcess = Start-Process -FilePath 'dotnet' -ArgumentList ('"' + $gatewayDll + '"') `
		-WorkingDirectory (Split-Path $gatewayDll) -WindowStyle Hidden -PassThru `
		-RedirectStandardOutput (Join-Path $runDirectory 'gateway.out') -RedirectStandardError (Join-Path $runDirectory 'gateway.err')
	if (-not (Wait-Until { try { $null -ne (Invoke-RestMethod ($gatewayUrl + '/health') -TimeoutSec 1) } catch { $false } } 30)) {
		throw 'Gateway health endpoint did not become ready.'
	}

	Write-Section 'tool discovery'
	$tools = @((Invoke-Mcp -Method 'tools/list' -Parameters @{}).tools)
	$modulesTool = @($tools | Where-Object { $_.name -eq 'list_modules' })[0]
	Assert-That 'list_modules advertises name_pattern, offset and count' (
		$null -ne $modulesTool.inputSchema.properties.name_pattern -and
		$null -ne $modulesTool.inputSchema.properties.offset -and
		$null -ne $modulesTool.inputSchema.properties.count)
	$documentsTool = @($tools | Where-Object { $_.name -eq 'list_documents' })[0]
	Assert-That 'list_documents advertises the same three' (
		$null -ne $documentsTool.inputSchema.properties.name_pattern -and
		$null -ne $documentsTool.inputSchema.properties.offset -and
		$null -ne $documentsTool.inputSchema.properties.count)
	$textTool = @($tools | Where-Object { $_.name -eq 'search_text' })[0]
	Assert-That 'search_text advertises a resume cursor' ($null -ne $textTool.inputSchema.properties.scan_offset)
	$analyzeTool = @($tools | Where-Object { $_.name -eq 'analyze_symbol' })[0]
	Assert-That 'analyze_symbol advertises a resume cursor' ($null -ne $analyzeTool.inputSchema.properties.scan_offset)
	Assert-That 'analyze_symbol advertises max_scan' ($null -ne $analyzeTool.inputSchema.properties.max_scan)
	# The old 5000 ceiling was low enough to make a 7227-method module unreachable outright.
	Assert-That 'the analyze_symbol work bound reaches past 5000' ($analyzeTool.inputSchema.properties.max_scan.maximum -gt 5000)

	Write-Section 'attach to the Mono endpoint'
	$session = Invoke-Tool -Name 'attach_endpoint' -Arguments @{
		address = $AgentAddress; port = $AgentPort; engine = 'unity'
		process_is_suspended = $AgentSuspended; connection_timeout_ms = 30000
	}
	Assert-That 'attach_endpoint did not fault' ($session.state -ne 'faulted') "state=$($session.state) $($session.fault_message)"
	$script:activeSessionId = $session.session_id
	$session_id = $session.session_id

	# suspend=y parks the runtime before any managed code runs, so Assembly-CSharp is not loaded yet.
	if ($AgentSuspended) { Invoke-MutatingTool -Name 'continue' -Arguments @{} | Out-Null }
	$harnessLoaded = Wait-Until {
		(Invoke-Tool -Name 'list_modules' -Arguments @{ session_id = $session_id; name_pattern = 'Assembly-CSharp' }).total -gt 0
	} 60
	Assert-That 'the harness assembly loads once the target runs' $harnessLoaded

	Write-Section 'list_modules filters and pages'
	$allModules = Invoke-Tool -Name 'list_modules' -Arguments @{ session_id = $session_id; count = 500 }
	Assert-That 'list_modules answers with a page object, not a bare array' ($null -ne $allModules.total -and $null -ne $allModules.modules)
	Assert-That 'the session has modules' ($allModules.total -gt 0) "total=$($allModules.total)"
	Write-Host "        $($allModules.total) modules loaded" -ForegroundColor DarkGray

	$firstPage = Invoke-Tool -Name 'list_modules' -Arguments @{ session_id = $session_id; count = 1 }
	Assert-That 'count bounds the page without changing total' (@($firstPage.modules).Count -eq 1 -and $firstPage.total -eq $allModules.total)
	Assert-That 'a bounded page reports itself truncated' ($firstPage.truncated -eq ($allModules.total -gt 1))
	$secondPage = Invoke-Tool -Name 'list_modules' -Arguments @{ session_id = $session_id; count = 1; offset = 1 }
	Assert-That 'offset reports itself back' ($secondPage.offset -eq 1)
	Assert-That 'offset moves to a different module' (@($secondPage.modules)[0].name -ne @($firstPage.modules)[0].name)

	# Ordering has to be stable or paging silently drops and repeats rows.
	$repeat = Invoke-Tool -Name 'list_modules' -Arguments @{ session_id = $session_id; count = 500 }
	$namesA = (@($allModules.modules) | ForEach-Object { $_.name }) -join '|'
	$namesB = (@($repeat.modules) | ForEach-Object { $_.name }) -join '|'
	Assert-That 'module order is stable between identical calls' ($namesA -eq $namesB)

	$pagedModules = @(); for ($offset = 0; $offset -lt $allModules.total; $offset += 7) {
		$pagedModules += @((Invoke-Tool -Name 'list_modules' -Arguments @{ session_id = $session_id; count = 7; offset = $offset }).modules)
	}
	Assert-That 'paging by 7 reproduces the whole list exactly' (((@($pagedModules) | ForEach-Object { $_.name }) -join '|') -eq $namesA) "paged=$(@($pagedModules).Count) all=$($allModules.total)"

	$filtered = Invoke-Tool -Name 'list_modules' -Arguments @{ session_id = $session_id; name_pattern = 'Assembly-CSharp' }
	Assert-That 'name_pattern narrows the list' ($filtered.total -gt 0 -and $filtered.total -lt $allModules.total) "filtered=$($filtered.total) all=$($allModules.total)"
	Assert-That 'every filtered row actually matches' (@(@($filtered.modules) | Where-Object { $_.name -notlike '*Assembly-CSharp*' -and $_.filename -notlike '*Assembly-CSharp*' }).Count -eq 0)
	# The biggest match when the filter finds several, because size is the whole point: the reported
	# failures were on a 20311-method Assembly-CSharp, not on its 3786-method firstpass sibling.
	$exact = @(@($filtered.modules) | Where-Object { $_.name -eq 'Assembly-CSharp.dll' })[0]
	$moduleName = if ($null -ne $exact) { $exact.name } else { @($filtered.modules)[0].name }
	Assert-That 'a filtered row still reports breakpoint capability' ($null -ne @($filtered.modules)[0].can_set_breakpoint)

	Write-Section 'list_documents filters and pages'
	$allDocuments = Invoke-Tool -Name 'list_documents' -Arguments @{ session_id = $session_id; count = 500 }
	Assert-That 'list_documents answers with a page object' ($null -ne $allDocuments.total -and $null -ne $allDocuments.documents)
	Assert-That 'it sees the same modules list_modules does' ($allDocuments.total -eq $allModules.total) "documents=$($allDocuments.total) modules=$($allModules.total)"
	$filteredDocs = Invoke-Tool -Name 'list_documents' -Arguments @{ session_id = $session_id; name_pattern = 'Assembly-CSharp' }
	Assert-That 'name_pattern narrows documents the same way' ($filteredDocs.total -eq $filtered.total) "docs=$($filteredDocs.total) modules=$($filtered.total)"
	Assert-That 'a filtered document still reports its metadata state' ($null -ne @($filteredDocs.documents)[0].has_metadata)

	Write-Section 'the module-name rule is one rule'
	# The reported bug. Name and filename both carry the ".dll", so equality missed and so did the old
	# EndsWith("\\"+module) arm; search accepted the same string all along.
	$stem = [IO.Path]::GetFileNameWithoutExtension($moduleName)
	$metadataByStem = Invoke-Tool -Name 'get_metadata' -Arguments @{ session_id = $session_id; module = $stem }
	Assert-That "get_metadata resolves the bare stem '$stem'" ($metadataByStem.module -eq $moduleName) "(got '$($metadataByStem.module)')"
	$metadataByName = Invoke-Tool -Name 'get_metadata' -Arguments @{ session_id = $session_id; module = $moduleName }
	Assert-That 'and the full name still resolves to the same module' ($metadataByName.module -eq $metadataByStem.module)
	$metadataByCase = Invoke-Tool -Name 'get_metadata' -Arguments @{ session_id = $session_id; module = $moduleName.ToUpperInvariant() }
	Assert-That 'case is not the defect and never was' ($metadataByCase.module -eq $metadataByStem.module)
	Write-Host "        $($metadataByName.module): $($metadataByName.type_count) types, $($metadataByName.method_count) methods" -ForegroundColor DarkGray

	# The filtering half of the family has to accept exactly what the resolving half does.
	$filterByStem = Invoke-Tool -Name 'search' -Arguments @{ session_id = $session_id; pattern = '/./'; kinds = @('module'); module = $stem; count = 10 }
	Assert-That 'a name that resolves also filters' (@($filterByStem.hits).Count -gt 0)

	$missing = Invoke-Tool -Name 'get_metadata' -Arguments @{ session_id = $session_id; module = ($stem -replace '-','') } -ExpectError
	Assert-That 'a near-miss module name is refused with candidates, not with a directory lookup' ($missing -match [regex]::Escape($moduleName)) "message=$missing"

	Write-Section 'search_text resumes where it stopped'
	# A single type, so the exhaustion check below stays cheap on a module with twenty thousand methods.
	$subjectTypeFilter = @((Invoke-Tool -Name 'list_types' -Arguments @{ session_id = $session_id; module = $moduleName; count = 1 }).symbols)[0].full_name
	Assert-That 'the module has a type to narrow to' (-not [string]::IsNullOrWhiteSpace($subjectTypeFilter))
	# Bounded hard, so even a small module has to page. The property under test is that paged coverage
	# equals unpaged coverage: no gap (the reported failure) and no repeat.
	# One window of 60 methods, taken whole, then taken as three pages of 20. The two must describe
	# exactly the same 60 methods: a missing hit is the gap that lost three keybinds, a duplicated one
	# means a resumed page re-did work the previous page had already done.
	$textArgs = @{ session_id = $session_id; pattern = 'void'; module = $moduleName; count = 200 }
	$oneShot = Invoke-Tool -Name 'search_text' -Arguments ($textArgs + @{ max_methods = 60 })
	$chunked = @(); $offset = 0; $pages = 0
	for ($page = 0; $page -lt 3; $page++) {
		$slice = Invoke-Tool -Name 'search_text' -Arguments ($textArgs + @{ max_methods = 20; scan_offset = $offset })
		$chunked += @($slice.hits); $pages++
		$offset = $slice.next_scan_offset
		if (-not $slice.scan_truncated) { break }
	}
	Assert-That 'the window needed more than one page' ($pages -gt 1) "pages=$pages"
	$oneShotKeys = @(@($oneShot.hits) | ForEach-Object { "$($_.method_token):$($_.line)" }) | Sort-Object
	$pagedKeys = @(@($chunked) | ForEach-Object { "$($_.method_token):$($_.line)" }) | Sort-Object
	Assert-That 'the paged sweep found something at all' (@($pagedKeys).Count -gt 0)
	Assert-That 'paged coverage equals unpaged coverage: no gap' (@(@($oneShotKeys) | Where-Object { $_ -notin $pagedKeys }).Count -eq 0) "missed=$(@(@($oneShotKeys) | Where-Object { $_ -notin $pagedKeys }).Count) of $(@($oneShotKeys).Count)"
	# As a multiset, so a page that re-did the previous page's work shows up as extra copies.
	Assert-That 'and the pages reproduce it exactly, with no repeat and nothing extra' ((($pagedKeys) -join '|') -eq (($oneShotKeys) -join '|')) "paged=$(@($pagedKeys).Count) whole=$(@($oneShotKeys).Count)"

	# And the cursor has to terminate, not merely advance. Narrowed by type so this stays cheap on a
	# module with twenty thousand methods.
	$narrow = @{ session_id = $session_id; pattern = 'void'; module = $moduleName; type = $subjectTypeFilter; count = 200; max_methods = 5 }
	$exhausted = Invoke-Paged -Name 'search_text' -HitsProperty 'hits' -Arguments $narrow -MaxCalls 60
	Assert-That 'paging a narrowed scope reaches the end' ($exhausted.Calls -ge 1)

	# The signal an agent misread. Only scan_truncated:false means the sweep is finished.
	$stopped = Invoke-Tool -Name 'search_text' -Arguments @{ session_id = $session_id; pattern = 'void'; module = $moduleName; max_methods = 1 }
	Assert-That 'a spent bound says so' ($stopped.scan_truncated -eq $true)
	Assert-That 'and hands back somewhere to continue' ($stopped.next_scan_offset -ge 1)
	Assert-That 'scanned_methods counts work done past the cursor' ($stopped.scanned_methods -eq 1) "scanned=$($stopped.scanned_methods)"

	Write-Section 'analyze_symbol resumes where it stopped'
	# Pick a method that is actually called, so "0 edges" is a real failure rather than a true answer.
	$callees = Invoke-Tool -Name 'search' -Arguments @{ session_id = $session_id; pattern = 'Update'; kinds = @('method'); module = $moduleName; count = 50 }
	$subject = @(@($callees.hits) | Where-Object { $_.token -gt 0 })[0]
	if ($null -eq $subject) {
		$anyMethod = Invoke-Tool -Name 'search' -Arguments @{ session_id = $session_id; pattern = '/./'; kinds = @('method'); module = $moduleName; count = 50 }
		$subject = @(@($anyMethod.hits) | Where-Object { $_.token -gt 0 })[0]
	}
	Assert-That 'a subject method was found to analyze' ($null -ne $subject)

	# Same window property as search_text: 3000 slots whole, then as three pages of 1000.
	$analyzeArgs = @{ session_id = $session_id; module = $moduleName; token = $subject.token; search_module = $moduleName; count = 500 }
	$analyzeWhole = Invoke-Tool -Name 'analyze_symbol' -Arguments ($analyzeArgs + @{ max_scan = 3000 })
	Write-Host "        $($analyzeWhole.scanned) slots inspected, $($analyzeWhole.total) edges, truncated=$($analyzeWhole.scan_truncated)" -ForegroundColor DarkGray
	$analyzeChunks = @(); $offset = 0; $pages = 0
	for ($page = 0; $page -lt 3; $page++) {
		$slice = Invoke-Tool -Name 'analyze_symbol' -Arguments ($analyzeArgs + @{ max_scan = 1000; scan_offset = $offset })
		$analyzeChunks += @($slice.edges); $pages++
		$offset = $slice.next_scan_offset
		if (-not $slice.scan_truncated) { break }
	}
	Assert-That 'the analyze window needed more than one page' ($pages -gt 1) "pages=$pages"
	$wholeEdges = @(@($analyzeWhole.edges) | ForEach-Object { "$($_.kind):$($_.source.method_token):$($_.target.method_token)" }) | Sort-Object
	$pagedEdges = @(@($analyzeChunks) | ForEach-Object { "$($_.kind):$($_.source.method_token):$($_.target.method_token)" }) | Sort-Object
	Assert-That 'paged edges cover the single-call edges: no gap' (@(@($wholeEdges) | Where-Object { $_ -notin $pagedEdges }).Count -eq 0) "missed=$(@(@($wholeEdges) | Where-Object { $_ -notin $pagedEdges }).Count) of $(@($wholeEdges).Count)"
	# Compared as a multiset, not a set. An edge key legitimately repeats -- a method that calls another
	# twice produces two identical "callee" edges -- so de-duplicating before comparing would hide the
	# failure this is for: type-level edges come out of the same traversal as the method edges, so a
	# cursor that counted only method bodies would re-emit every one of them on every resumed page, and
	# the extra copies would vanish into a set comparison.
	Assert-That 'and the pages reproduce it exactly, with no repeat and nothing extra' ((($pagedEdges) -join '|') -eq (($wholeEdges) -join '|')) "paged=$(@($pagedEdges).Count) whole=$(@($wholeEdges).Count)"

	# The measured failure: at the old 5000 ceiling this module answered "0 edges, scan_truncated" and
	# there was nowhere to go from there. Paging to exhaustion has to terminate and find the callers.
	$analyzeExhausted = Invoke-Paged -Name 'analyze_symbol' -HitsProperty 'edges' -MaxCalls 400 `
		-Arguments ($analyzeArgs + @{ max_scan = 200000 })
	Write-Host "        exhausted in $($analyzeExhausted.Calls) call(s), $(@($analyzeExhausted.Items).Count) edges" -ForegroundColor DarkGray
	Assert-That 'paging analyze_symbol to exhaustion terminates' ($analyzeExhausted.Calls -ge 1)
	Assert-That 'a symbol beyond one bound is reachable at all' (@($analyzeExhausted.Items).Count -ge @($wholeEdges).Count) "exhausted=$(@($analyzeExhausted.Items).Count) window=$(@($wholeEdges).Count)"

	# The exact shape of the reported failure, asserted only where the module is actually big enough to
	# have it: at the old max_methods=5000 ceiling this module answered "scan_truncated" with a region
	# behind it that no setting could reach.
	if ($analyzeExhausted.Reached -gt 5000) {
		$oldCeiling = Invoke-Tool -Name 'analyze_symbol' -Arguments ($analyzeArgs + @{ max_scan = 5000 })
		Assert-That 'the old 5000-slot ceiling could not reach the end of this module' ($oldCeiling.scan_truncated -eq $true)
		Assert-That 'and everything past it is reachable now' ($analyzeExhausted.Reached -gt $oldCeiling.next_scan_offset) "reached=$($analyzeExhausted.Reached) old=$($oldCeiling.next_scan_offset)"
	}
	else {
		Write-Host "        skipped: $moduleName is only $($analyzeExhausted.Reached) slots, inside the old ceiling" -ForegroundColor DarkGray
	}

	$analyzeStopped = Invoke-Tool -Name 'analyze_symbol' -Arguments ($analyzeArgs + @{ max_scan = 1 })
	Assert-That 'a spent analyze bound says so and offers a resume point' ($analyzeStopped.scan_truncated -eq $true -and $analyzeStopped.next_scan_offset -ge 1)
	# max_methods is the older spelling of the same bound; a caller that learned it must keep working.
	$legacyBound = Invoke-Tool -Name 'analyze_symbol' -Arguments ($analyzeArgs + @{ max_methods = 1 })
	Assert-That 'the older max_methods spelling still bounds the walk' ($legacyBound.scan_truncated -eq $true)

	Write-Section 'resume and detach'
	$state = Invoke-Tool -Name 'get_session_state' -Arguments @{ session_id = $script:activeSessionId }
	$detached = Invoke-Tool -Name 'detach' -Arguments @{ session_id = $script:activeSessionId; expected_lifecycle_version = $state.lifecycle_version }
	Assert-That 'detach reports detached, not terminated' ($detached.detached -eq $true -and $detached.terminated -ne $true)
	$script:activeSessionId = ''

	# The player must outlive the debugger. Observe it, do not connect to it.
	Start-Sleep -Seconds 2
	Assert-That 'the player survives detach' (@(Get-NetTCPConnection -State Listen -LocalPort $AgentPort -ErrorAction SilentlyContinue).Count -gt 0)
}
finally {
	if ($gatewayProcess) { Stop-Process -Id $gatewayProcess.Id -Force -ErrorAction SilentlyContinue }
	if ($dnSpyHostId) { Stop-Process -Id $dnSpyHostId -Force -ErrorAction SilentlyContinue }
	Pop-Location
}

Write-Host ''
if ($script:failures.Count -gt 0) {
	Write-Host "FAILED  $($script:failures.Count) of $script:checks checks" -ForegroundColor Red
	$script:failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
	Write-Host "Logs: $runDirectory"
	exit 1
}
Write-Host "PASSED  $script:checks checks" -ForegroundColor Green
exit 0
