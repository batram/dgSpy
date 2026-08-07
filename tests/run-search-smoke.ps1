<#
.SYNOPSIS
	Live smoke for the `search` tool against the uch-debug-target Unity player.

.DESCRIPTION
	The unit tests in tests\dgSpy.Extension.Tests cover the ported matching rules against strings.
	They cannot cover the thing that actually broke: a real dnlib walk over a real module, where the
	declaring-type name that has to be reconstructed comes from FullNameFactory rather than a literal
	in a test file. This runs the tool against a live Mono/Unity target and asserts the behaviour that
	was reported as broken.

	The graph in UchDebugTarget\Assets\Scripts\DeepGraph.cs happens to reproduce every reported shape:

	  Level4.Child      a field whose type is another type   (the GameState.ChatSystem case)
	  Level5.Label      a property, not a field               (the Runspace.DefaultRunspace case)
	  Box<T>            a generic type, so `1 must be stripped
	  "deepest"         a string literal in a method body     (the Number/String case)

	UchDebugTarget\Assets\Scripts\SearchProbe.cs was added for the categories nothing else in the
	target had: nested and nested-generic types (whose metadata names use '/'), an enum, a delegate,
	an event, constant fields whose values live in the Constant table rather than in IL, and a
	distinctively named parameter and local. Every identifier there ends in "Probe" so a check can
	assert an exact hit, instead of matching a framework symbol by accident.

	Note that this player carries no local-variable debug info, so kinds:["local"] finds nothing here.
	That is a property of a release Unity build, not of the tool; the check reports which case it saw
	rather than asserting either.

	This script attaches to an already-listening agent. Launching the player is the caller's job
	(tools\Launch-Target.ps1 in the batram/uch-debug-target repo), for the reasons run-mono-target-smoke
	gives: Unity silently ignores a malformed --debugger-agent, and readiness must be observed rather
	than connected to.

	ASCII-only by policy: an em dash in a .ps1 parses as a stray quote under CP1252.

.EXAMPLE
	.\tests\run-search-smoke.ps1

.EXAMPLE
	.\tests\run-search-smoke.ps1 -AgentPort 56000 -TargetFramework net10.0-windows
#>
[CmdletBinding()]
param(
	[string]$AgentAddress = '127.0.0.1',
	# 56000, deliberately not the real game's 55555, so a live UCH can never be mistaken for this.
	[int]$AgentPort = 56000,
	[bool]$AgentSuspended = $true,
	[ValidateSet('net10.0-windows','net48')][string]$TargetFramework = 'net10.0-windows',
	# Distinct from every other smoke's ports so two can run without colliding.
	[int]$GatewayPort = 17362,
	[int]$RpcPort = 7363
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

$runDirectory = Join-Path $env:TEMP ("dgspy-search-smoke-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $runDirectory -Force | Out-Null

# Returns the single hit whose name matches, or $null. Written out because Windows PowerShell 5.1
# collapses a one-element array on the pipeline, which silently turns "exactly one hit" into a
# passing check that never looked at anything.
function Select-Hit {
	param($Result, [string]$Name, [string]$Kind)
	@(@($Result.hits) | Where-Object { $_.name -eq $Name -and (-not $Kind -or $_.kind -eq $Kind) })[0]
}

try {
	Write-Section 'preflight'
	# Observe the agent, never connect to it. A connect-close without completing the DWP handshake
	# wedges the agent, and a bare connect while the runtime is still suspended awaiting its first
	# debugger kills the player outright. Both present as debugger bugs.
	$listening = @(Get-NetTCPConnection -State Listen -LocalPort $AgentPort -ErrorAction SilentlyContinue).Count -gt 0
	if (-not $listening) {
		throw "No listener on $AgentAddress`:$AgentPort. Launch the target first: tools\Launch-Target.ps1 -PlayerRoot <player> -Port $AgentPort"
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
	$searchTool = @($tools | Where-Object { $_.name -eq 'search' })[0]
	Assert-That 'the gateway advertises search' ($null -ne $searchTool)
	Assert-That 'search is advertised read-only' ($searchTool.annotations.readOnlyHint -eq $true)
	# session_id must NOT be required, or the documents scope cannot be reached without a target.
	Assert-That 'search requires only pattern' ((@($searchTool.inputSchema.required) -join ',') -eq 'pattern')
	Assert-That 'search offers all 24 dnSpy search categories' (@($searchTool.inputSchema.properties.kinds.items.enum).Count -eq 24)
	Assert-That 'the older search_symbols is still advertised' (@($tools | Where-Object { $_.name -eq 'search_symbols' }).Count -eq 1)

	Write-Section 'documents scope without a session'
	# The one behaviour that differs from every neighbouring tool, exercised before anything attaches
	# so that "no session" is a fact rather than an assumption.
	$documents = Invoke-Tool -Name 'search' -Arguments @{ pattern = 'Object'; scope = 'documents'; count = 5 }
	Assert-That 'documents scope answers with no session_id' ($null -ne $documents.total)
	Assert-That 'documents scope reports its work bound' ($null -ne $documents.scan_truncated)
	Assert-That 'no documents-scope hit claims to be in the session' (@(@($documents.hits) | Where-Object { $_.in_session -eq $true }).Count -eq 0)
	# Session scope without a session must still be refused, or the optional session_id would have
	# quietly removed the guard from the default path too.
	$refused = Invoke-Tool -Name 'search' -Arguments @{ pattern = 'Object' } -ExpectError
	Assert-That 'session scope still demands a live session' ($refused -match 'session')

	Write-Section 'attach to the Mono endpoint'
	$session = Invoke-Tool -Name 'attach_endpoint' -Arguments @{
		address = $AgentAddress; port = $AgentPort; engine = 'unity'
		process_is_suspended = $AgentSuspended; connection_timeout_ms = 30000
	}
	Assert-That 'attach_endpoint did not fault' ($session.state -ne 'faulted') "state=$($session.state) $($session.fault_message)"
	$script:activeSessionId = $session.session_id

	# suspend=y parks the runtime before any managed code runs, so Assembly-CSharp is not loaded yet
	# and searching would prove nothing.
	Invoke-MutatingTool -Name 'continue' -Arguments @{} | Out-Null
	$harnessLoaded = Wait-Until {
		@(Invoke-Tool -Name 'list_modules' -Arguments @{ session_id = $script:activeSessionId } |
			Where-Object { $_.name -like 'Assembly-CSharp*' }).Count -gt 0
	} 60
	Assert-That 'the harness assembly loads once the target runs' $harnessLoaded
	$moduleFilter = 'Assembly-CSharp'
	$session_id = $script:activeSessionId

	Write-Section 'the reported bug: a qualified member path'
	$type = Invoke-Tool -Name 'search' -Arguments @{ session_id = $session_id; pattern = 'UchDebugTarget.Level4'; kinds = @('type'); module = $moduleFilter }
	$typeHit = Select-Hit $type 'Level4' 'type'
	Assert-That 'search finds a type by its namespace-qualified name' ($null -ne $typeHit)
	Assert-That 'the type hit is reported as in-session' ($typeHit.in_session -eq $true)

	# Level4.Child is a field whose type is Level5: structurally identical to GameState.ChatSystem,
	# the field an agent was told did not exist.
	$dotted = Invoke-Tool -Name 'search' -Arguments @{ session_id = $session_id; pattern = 'Level4.Child'; kinds = @('field'); module = $moduleFilter }
	$fieldHit = Select-Hit $dotted 'Child' 'field'
	Assert-That 'search resolves Type.Member, which is the whole point' ($null -ne $fieldHit) "total=$($dotted.total)"
	Assert-That 'the field hit names its declaring type' ($fieldHit.declaring_type -eq 'UchDebugTarget.Level4')
	Assert-That 'the field hit carries a metadata token' ($fieldHit.token -gt 0)

	# dnSpy's GUI accepts the IL-style separator too.
	$colons = Invoke-Tool -Name 'search' -Arguments @{ session_id = $session_id; pattern = 'UchDebugTarget.Level4::Child'; kinds = @('field'); module = $moduleFilter }
	Assert-That 'search resolves the Type::Member form as dnSpy does' ($null -ne (Select-Hit $colons 'Child' 'field'))

	# The delta, measured rather than asserted. If search_symbols is ever fixed this check tells us,
	# instead of the smoke silently protecting a claim that stopped being true.
	$old = Invoke-Tool -Name 'search_symbols' -Arguments @{ session_id = $session_id; pattern = 'Level4.Child'; kinds = @('field'); module = $moduleFilter }
	Assert-That 'search_symbols still cannot resolve a qualified path' ($old.total -eq 0) "total=$($old.total)"

	Write-Section 'round-tripping'
	# The 3fcbacd73 rule: a tool must never emit an identifier it would then reject.
	$again = Invoke-Tool -Name 'search' -Arguments @{ session_id = $session_id; pattern = $fieldHit.full_name; kinds = @('field'); module = $moduleFilter }
	$againHit = Select-Hit $again 'Child' 'field'
	Assert-That 'a returned full_name is accepted back as a pattern' ($null -ne $againHit) "full_name=$($fieldHit.full_name)"
	Assert-That 'the round trip finds the same symbol' ($againHit.token -eq $fieldHit.token)

	$members = Invoke-Tool -Name 'list_members' -Arguments @{ session_id = $session_id; module = $fieldHit.module; type = $fieldHit.declaring_type }
	Assert-That 'a returned declaring_type is accepted by list_members' (@(@($members.symbols) | Where-Object { $_.name -eq 'Child' }).Count -eq 1)

	$il = Invoke-Tool -Name 'get_metadata' -Arguments @{ session_id = $session_id; module = $fieldHit.module; token = $fieldHit.token }
	Assert-That 'a returned module and token resolve in get_metadata' ($il.token_full_name -match 'Child')

	Write-Section 'kinds beyond type, method and field'
	# Level5.Label is a property. This is the Runspace.DefaultRunspace case: with no property kind,
	# an agent has to guess at backing-field names.
	$properties = Invoke-Tool -Name 'search' -Arguments @{ session_id = $session_id; pattern = 'Level5.Label'; kinds = @('property'); module = $moduleFilter }
	$propertyHit = Select-Hit $properties 'Label' 'property'
	Assert-That 'search finds a property by its qualified path' ($null -ne $propertyHit) "total=$($properties.total)"
	Assert-That 'the property hit is a property, not its backing field' ($propertyHit.kind -eq 'property')

	# Category discrimination: asking for interfaces must not answer with classes.
	$interfaces = Invoke-Tool -Name 'search' -Arguments @{ session_id = $session_id; pattern = 'ILabeled'; kinds = @('interface'); module = $moduleFilter }
	Assert-That 'kinds:[interface] finds the interface' ($null -ne (Select-Hit $interfaces 'ILabeled' 'type'))
	$asClass = Invoke-Tool -Name 'search' -Arguments @{ session_id = $session_id; pattern = 'ILabeled'; kinds = @('class'); module = $moduleFilter }
	Assert-That 'kinds:[class] does not return an interface' ($null -eq (Select-Hit $asClass 'ILabeled' 'type')) "total=$($asClass.total)"
	$asInterface = Invoke-Tool -Name 'search' -Arguments @{ session_id = $session_id; pattern = 'Level5'; kinds = @('interface'); module = $moduleFilter }
	Assert-That 'kinds:[interface] does not return a sealed class' ($null -eq (Select-Hit $asInterface 'Level5' 'type')) "total=$($asInterface.total)"

	# Box<T> is Box`1 in metadata. dnSpy strips the arity, and so must we, or the name the GUI shows
	# would not be a name this tool accepts.
	$generic = Invoke-Tool -Name 'search' -Arguments @{ session_id = $session_id; pattern = 'UchDebugTarget.Box'; kinds = @('generic_type'); module = $moduleFilter }
	$genericHit = @(@($generic.hits) | Where-Object { $_.full_name -eq 'UchDebugTarget.Box' })[0]
	Assert-That 'a generic type is found by its arity-stripped name' ($null -ne $genericHit) "total=$($generic.total)"

	Write-Section 'literal search'
	# "deepest" is a field initializer, so it is an ldstr in Level5's constructor. This is the
	# Number/String dropdown entry, and it reads IL rather than decompiling.
	$literal = Invoke-Tool -Name 'search' -Arguments @{ session_id = $session_id; pattern = '"deepest"'; kinds = @('literal'); module = $moduleFilter }
	Assert-That 'literal search finds a string constant in IL' ($literal.total -gt 0) "total=$($literal.total)"
	$literalHit = @($literal.hits)[0]
	Assert-That 'a literal hit says why it matched' ($literalHit.match_context -match 'deepest')
	Assert-That 'a literal hit is still debugger-addressable' ($literalHit.token -gt 0)

	$number = Invoke-Tool -Name 'search' -Arguments @{ session_id = $session_id; pattern = '12345678'; kinds = @('literal'); module = $moduleFilter }
	Assert-That 'an absent numeric literal returns nothing rather than failing' ($number.total -eq 0)

	# Mixing literal with a name kind would mean neither thing, so it is refused.
	$mixed = Invoke-Tool -Name 'search' -Arguments @{ session_id = $session_id; pattern = 'x'; kinds = @('literal','type'); module = $moduleFilter } -ExpectError
	Assert-That 'literal cannot be silently combined with name kinds' ($mixed -match 'literal')
	$bogus = Invoke-Tool -Name 'search' -Arguments @{ session_id = $session_id; pattern = 'x'; kinds = @('propery') } -ExpectError
	Assert-That 'a misspelled kind is reported, not ignored' ($bogus -match 'propery')

	Write-Section 'pattern syntax'
	$regex = Invoke-Tool -Name 'search' -Arguments @{ session_id = $session_id; pattern = '/^UchDebugTarget\.Level[0-9]$/'; kinds = @('type'); module = $moduleFilter }
	Assert-That 'slashes make the pattern a regular expression' ($regex.total -eq 5) "total=$($regex.total)"
	$anded = Invoke-Tool -Name 'search' -Arguments @{ session_id = $session_id; pattern = 'Level Child'; kinds = @('field'); module = $moduleFilter }
	Assert-That 'space-separated terms are ANDed as dnSpy does' ($anded.total -gt 0 -and $anded.total -lt $regex.total + 20) "total=$($anded.total)"
	$whole = Invoke-Tool -Name 'search' -Arguments @{ session_id = $session_id; pattern = 'Level'; kinds = @('type'); whole_word = $true; module = $moduleFilter }
	Assert-That 'whole_word rejects a partial name' ($whole.total -eq 0) "total=$($whole.total)"

	Write-Section 'nested types'
	# Metadata spells a nested type Outer/Inner, and a generic one Outer/Inner`1. dnSpy presents both
	# with dots and no arity, and a name a tool prints has to be a name that tool accepts. This is the
	# one part of the ported FixTypeName that a string unit test cannot reach: the input here comes from
	# dnlib's FullNameFactory against real metadata rather than a literal in a test file.
	$nested = Invoke-Tool -Name 'search' -Arguments @{ session_id = $session_id; pattern = 'UchDebugTarget.SearchProbe.NestedMarkerProbe'; kinds = @('type'); module = $moduleFilter }
	$nestedHit = Select-Hit $nested 'NestedMarkerProbe' 'type'
	Assert-That 'a nested type is found by its dotted name' ($null -ne $nestedHit) "total=$($nested.total)"
	Assert-That 'a nested type is presented with dots, not slashes' ($nestedHit.full_name -eq 'UchDebugTarget.SearchProbe.NestedMarkerProbe')
	Assert-That 'the nested full_name round-trips' ($null -ne (Select-Hit (Invoke-Tool -Name 'search' -Arguments @{ session_id = $session_id; pattern = $nestedHit.full_name; kinds = @('type'); module = $moduleFilter }) 'NestedMarkerProbe' 'type'))

	$deeper = Invoke-Tool -Name 'search' -Arguments @{ session_id = $session_id; pattern = 'SearchProbe.NestedMarkerProbe.DeeperMarkerProbe'; kinds = @('type'); module = $moduleFilter }
	Assert-That 'a twice-nested type resolves through both separators' ($null -ne (Select-Hit $deeper 'DeeperMarkerProbe' 'type')) "total=$($deeper.total)"

	$nestedGeneric = Invoke-Tool -Name 'search' -Arguments @{ session_id = $session_id; pattern = 'UchDebugTarget.SearchProbe.NestedBoxProbe'; kinds = @('generic_type'); module = $moduleFilter }
	$nestedGenericHit = Select-Hit $nestedGeneric 'NestedBoxProbe' 'type'
	Assert-That 'a nested generic drops both the slash and the arity' ($null -ne $nestedGenericHit) "total=$($nestedGeneric.total)"
	Assert-That 'the nested generic full_name has no backtick' ($nestedGenericHit.full_name -eq 'UchDebugTarget.SearchProbe.NestedBoxProbe')

	Write-Section 'the remaining type categories'
	$enum = Invoke-Tool -Name 'search' -Arguments @{ session_id = $session_id; pattern = 'ProbeColorEnum'; kinds = @('enum'); module = $moduleFilter }
	Assert-That 'kinds:[enum] finds the enum' ($null -ne (Select-Hit $enum 'ProbeColorEnum' 'type'))
	# An enum is a value type. It must not be swept up by a struct query, or the categories are noise.
	$enumAsStruct = Invoke-Tool -Name 'search' -Arguments @{ session_id = $session_id; pattern = 'ProbeColorEnum'; kinds = @('struct'); module = $moduleFilter }
	Assert-That 'kinds:[struct] does not return an enum' ($null -eq (Select-Hit $enumAsStruct 'ProbeColorEnum' 'type')) "total=$($enumAsStruct.total)"
	$struct = Invoke-Tool -Name 'search' -Arguments @{ session_id = $session_id; pattern = 'ProbeShapeStruct'; kinds = @('struct'); module = $moduleFilter }
	Assert-That 'kinds:[struct] finds the struct' ($null -ne (Select-Hit $struct 'ProbeShapeStruct' 'type'))
	$structAsClass = Invoke-Tool -Name 'search' -Arguments @{ session_id = $session_id; pattern = 'ProbeShapeStruct'; kinds = @('class'); module = $moduleFilter }
	Assert-That 'kinds:[class] does not return a struct' ($null -eq (Select-Hit $structAsClass 'ProbeShapeStruct' 'type')) "total=$($structAsClass.total)"
	$delegateType = Invoke-Tool -Name 'search' -Arguments @{ session_id = $session_id; pattern = 'ProbeCallbackDelegate'; kinds = @('delegate'); module = $moduleFilter }
	Assert-That 'kinds:[delegate] finds the delegate' ($null -ne (Select-Hit $delegateType 'ProbeCallbackDelegate' 'type'))
	$delegateAsClass = Invoke-Tool -Name 'search' -Arguments @{ session_id = $session_id; pattern = 'ProbeCallbackDelegate'; kinds = @('class'); module = $moduleFilter }
	Assert-That 'kinds:[class] does not return a delegate' ($null -eq (Select-Hit $delegateAsClass 'ProbeCallbackDelegate' 'type')) "total=$($delegateAsClass.total)"

	Write-Section 'events, properties, parameters and locals'
	$events = Invoke-Tool -Name 'search' -Arguments @{ session_id = $session_id; pattern = 'SearchProbe.FiredProbe'; kinds = @('event'); module = $moduleFilter }
	$eventHit = Select-Hit $events 'FiredProbe' 'event'
	Assert-That 'an event resolves by its qualified path' ($null -ne $eventHit) "total=$($events.total)"
	# The compiler emits a backing field of the same name. The event kind must return the event.
	Assert-That 'the event hit is the event, not its backing field' ($eventHit.kind -eq 'event')

	$probeProperty = Invoke-Tool -Name 'search' -Arguments @{ session_id = $session_id; pattern = 'ProbeMarkerImpl.RankProbe'; kinds = @('property'); module = $moduleFilter }
	Assert-That 'a property on an interface implementation resolves' ($null -ne (Select-Hit $probeProperty 'RankProbe' 'property')) "total=$($probeProperty.total)"

	# Parameter names live in the metadata Param table, so they are searchable with no debug info.
	$parameters = Invoke-Tool -Name 'search' -Arguments @{ session_id = $session_id; pattern = 'measuredValueProbe'; kinds = @('parameter'); module = $moduleFilter }
	$parameterHit = Select-Hit $parameters 'MeasureProbe' 'method'
	Assert-That 'kinds:[parameter] finds the method owning the parameter' ($null -ne $parameterHit) "total=$($parameters.total)"
	Assert-That 'a parameter hit says which parameter matched' ($parameterHit.match_context -match 'measuredValueProbe')
	Assert-That 'a parameter hit is still debugger-addressable' ($parameterHit.token -gt 0)

	# Local names come from debug info, not metadata, so a release player may carry none. Assert the
	# call is well formed either way and report which case this target is, rather than asserting a
	# result that depends on whether a PDB happened to ship.
	$locals = Invoke-Tool -Name 'search' -Arguments @{ session_id = $session_id; pattern = 'scratchpadLocalProbe'; kinds = @('local'); module = $moduleFilter }
	Assert-That 'kinds:[local] returns a well-formed bounded result' ($null -ne $locals.total -and $null -ne $locals.scan_truncated)
	Write-Host "        local names available in this player: $($locals.total -gt 0)" -ForegroundColor DarkGray

	Write-Section 'literals from metadata and from IL'
	# A const lives in the Constant table and is never an ldc instruction. A field initializer IS an
	# ldstr in the constructor. Both are dnSpy's Number/String entry, and only covering one of them
	# would leave half the feature untested.
	$constInt = Invoke-Tool -Name 'search' -Arguments @{ session_id = $session_id; pattern = '424242'; kinds = @('literal'); module = $moduleFilter }
	$constIntHit = Select-Hit $constInt 'MagicNumberProbe' 'field'
	Assert-That 'a constant integer is found in the Constant table' ($null -ne $constIntHit) "total=$($constInt.total)"
	Assert-That 'a constant hit says it matched a constant' ($constIntHit.match_context -match 'constant')
	$constHex = Invoke-Tool -Name 'search' -Arguments @{ session_id = $session_id; pattern = '0x67932'; kinds = @('literal'); module = $moduleFilter }
	Assert-That 'the same constant is found written in hex' ($null -ne (Select-Hit $constHex 'MagicNumberProbe' 'field'))

	$constString = Invoke-Tool -Name 'search' -Arguments @{ session_id = $session_id; pattern = '"probe-signature-marker"'; kinds = @('literal'); module = $moduleFilter }
	Assert-That 'a constant string is found in the Constant table' ($null -ne (Select-Hit $constString 'SignatureProbe' 'field')) "total=$($constString.total)"

	$emitted = Invoke-Tool -Name 'search' -Arguments @{ session_id = $session_id; pattern = '"probe-emitted-marker"'; kinds = @('literal'); module = $moduleFilter }
	Assert-That 'a field initializer is found as an ldstr in IL' ($emitted.total -gt 0) "total=$($emitted.total)"
	Assert-That 'the IL literal hit names the instruction' (@($emitted.hits)[0].match_context -match 'ldstr')

	Write-Section 'boundedness and the resume cursor'
	$unbounded = Invoke-Tool -Name 'search' -Arguments @{ session_id = $session_id; pattern = 'Level'; kinds = @('type','member'); module = $moduleFilter; count = 500 }
	Assert-That 'an unbounded search exhausts its scope' ($unbounded.scan_truncated -eq $false)
	Assert-That 'the scope contains something to find' ($unbounded.total -gt 0) "total=$($unbounded.total)"

	$page = Invoke-Tool -Name 'search' -Arguments @{ session_id = $session_id; pattern = 'Level'; kinds = @('type','member'); module = $moduleFilter; max_scan = 50; count = 500 }
	Assert-That 'max_scan stops the walk early' ($page.scan_truncated -eq $true)
	Assert-That 'max_scan bounds work, not output' ($page.scanned -le 50) "scanned=$($page.scanned)"
	Assert-That 'a truncated walk reports where it stopped' ($page.next_scan_offset -gt 0)

	# Resume until the scope is exhausted and confirm the pages sum to the single-call total. This is
	# the property that makes a large module reachable at all.
	$resumedTotal = $page.total
	$cursor = $page.next_scan_offset
	$pages = 1
	while ($page.scan_truncated -and $pages -lt 400) {
		$page = Invoke-Tool -Name 'search' -Arguments @{ session_id = $session_id; pattern = 'Level'; kinds = @('type','member'); module = $moduleFilter; max_scan = 50; count = 500; scan_offset = $cursor }
		if ($page.next_scan_offset -le $cursor) { break }
		$cursor = $page.next_scan_offset
		$resumedTotal += $page.total
		$pages++
	}
	Assert-That 'the resumed walk terminates' ($page.scan_truncated -eq $false) "pages=$pages cursor=$cursor"
	Assert-That 'resuming finds exactly what one unbounded call found' ($resumedTotal -eq $unbounded.total) "resumed=$resumedTotal unbounded=$($unbounded.total) pages=$pages"

	Write-Section 'a member path is not a type name'
	# The error that previously read "No type ... Use list_types" and led to a wrong answer.
	$wrong = Invoke-Tool -Name 'list_members' -Arguments @{ session_id = $session_id; module = $fieldHit.module; type = 'UchDebugTarget.Level4.Child' } -ExpectError
	Assert-That 'the error says Child is a field, not a missing type' ($wrong -match 'is a field')
	Assert-That 'the error names the type to ask for instead' ($wrong -match 'Level5')

	Write-Section 'scope: all'
	$all = Invoke-Tool -Name 'search' -Arguments @{ session_id = $session_id; pattern = 'UchDebugTarget.Level4'; kinds = @('type'); scope = 'all' }
	Assert-That 'scope all still finds the session symbol' (@(@($all.hits) | Where-Object { $_.name -eq 'Level4' }).Count -ge 1)
	Assert-That 'scope all reports which modules it walked' (@($all.modules_searched).Count -gt 0)
	# The debugger's metadata service and the Assembly Explorer return different ModuleDef instances for
	# one module, and against this target they do not agree on an MVID either, so a naive dedup walks
	# every module twice. That is not a cosmetic doubling: it doubles the work bound and reports every
	# hit twice.
	$duplicates = @($all.modules_searched | Group-Object | Where-Object { $_.Count -gt 1 })
	Assert-That 'scope all does not walk a module twice' ($duplicates.Count -eq 0) "dupes=$(($duplicates | ForEach-Object { $_.Name }) -join ',')"
	# When both views hold a module the session one must win, or the caller is handed in_session: false
	# for a module the session tools would have accepted.
	$sharedHit = @(@($all.hits) | Where-Object { $_.name -eq 'Level4' })[0]
	Assert-That 'a module held by both views is reported as in-session' ($sharedHit.in_session -eq $true)
	$allTotal = @(@($all.hits) | Where-Object { $_.name -eq 'Level4' }).Count
	Assert-That 'scope all reports the shared symbol exactly once' ($allTotal -eq 1) "count=$allTotal"

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
