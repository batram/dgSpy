#requires -Version 5.1
<#
.SYNOPSIS
	What HookLab does at Mono's hook-shape boundaries: generics and inlining. ASCII-only; hidden desktop.

.DESCRIPTION
	run-mono-hooklab-smoke.ps1 proves the lifecycle for the shape HookLab was designed around - a plain,
	non-generic, non-inlined static method. This gate asks the shapes nobody had measured, and asserts
	the property the roadmap actually asks for: every one of them produces a *decisive* answer.

	A decisive answer is either "installed, and the behaviour changed" or "refused, with a reason that
	names the shape". What this gate exists to forbid is the third outcome: an install that reports
	success while nothing about the target changed. That is the one result an operator cannot act on,
	and the one an untested boundary tends to produce.

	Same fixture, same launch, same Mono as the smoke gate, so a difference here is about the hook shape
	and nothing else.

.EXAMPLE
	$env:DGSPY_MONO_EXE = 'C:\Program Files\Mono\bin\mono.exe'
	.\tests\run-mono-hooklab-boundaries.ps1
#>
[CmdletBinding()]
param(
	[string]$MonoExe = $env:DGSPY_MONO_EXE,
	[string]$MonoRuntime = $env:DGSPY_MONO_RUNTIME,
	[string]$MonoAssemblies = $env:DGSPY_MONO_ASSEMBLIES,
	[int]$AgentPort = 0,
	[int]$RpcPort = 0,
	[ValidateSet('net10.0-windows','net48')][string]$TargetFramework = 'net10.0-windows',
	[string]$RunDirectory
)

$ErrorActionPreference = 'Stop'
$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new()
$repoRoot = Split-Path $PSScriptRoot -Parent
$targetExe = Join-Path $repoRoot 'tests\TestTargets\MonoHookLabTarget\bin\Release\net48\MonoHookLabTarget.exe'
$root = if ($RunDirectory) { $RunDirectory } else { Join-Path $PSScriptRoot ('artifacts\mono-boundaries-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
$hostId = 0; $target = $null; $sessionId = $null; $pass = 0; $fail = 0

. (Join-Path $PSScriptRoot 'TestSupport\Invoke-DgSpyRpc.ps1')
function Rpc([string]$Operation,[hashtable]$Arguments=@{},[int]$Deadline=70) { Invoke-DgSpyRpc -OperationName $Operation -OperationArguments $Arguments -RpcPort $RpcPort -DeadlineSeconds $Deadline }
function Check([string]$Name,$Condition,[string]$Detail='') { if([bool]$Condition){$script:pass++;Write-Host "PASS  $Name" -ForegroundColor DarkGreen}else{$script:fail++;Write-Host "FAIL  $Name  $Detail" -ForegroundColor Red} }
function Wait-Until([scriptblock]$Condition,[int]$Seconds=20) { $deadline=[DateTime]::UtcNow.AddSeconds($Seconds); do { if(& $Condition){return $true}; Start-Sleep -Milliseconds 200 } until([DateTime]::UtcNow -gt $deadline); return $false }
function Behavior { $value=(Get-Content -LiteralPath (Join-Path $root 'behavior.txt') -ErrorAction SilentlyContinue | Select-Object -First 1); if($value){[int]$value}else{-1} }

# Reflection over the file on disk, not the live target, matching run-mono-hooklab-smoke.ps1: the guards
# create_hook checks are properties of the assembly, and deriving them from the debugger would be asking
# the thing under test to describe itself. Signature is spelled the way MethodGuards.Signature spells it,
# including the fallback to Type.Name for a generic parameter, whose FullName is null.
function Get-MethodFacts([string]$AssemblyPath,[string]$TypeName,[string]$MethodName) {
	$assembly = [Reflection.Assembly]::LoadFrom($AssemblyPath)
	$method = $assembly.GetType($TypeName,$true).GetMethod($MethodName,[Reflection.BindingFlags]'Static,Public')
	$il = $method.GetMethodBody().GetILAsByteArray()
	if (-not $il) { throw "reflection did not find a body for $TypeName.$MethodName" }
	$sha = [Security.Cryptography.SHA256]::Create()
	try { $digest = [BitConverter]::ToString($sha.ComputeHash($il)).Replace('-','').ToLowerInvariant() } finally { $sha.Dispose() }
	[pscustomobject]@{
		Token = [uint32]$method.MetadataToken
		Mvid = $method.Module.ModuleVersionId.ToString('D')
		Signature = (Get-GuardTypeName $method.ReturnType) + ' ' + $MethodName + '(' + (($method.GetParameters() | ForEach-Object { Get-GuardTypeName $_.ParameterType }) -join ',') + ')'
		IlSha256 = $digest
	}
}

# FullName is null for a generic parameter, and MethodGuards.TypeName falls back to Name for exactly
# that case. Spelling it the same way is what makes the offered signature match the one the resident
# computes - otherwise the refusal under test would be a signature mismatch instead.
function Get-GuardTypeName($Type) { if ($Type.FullName) { $Type.FullName } else { $Type.Name } }

# One boundary case, asked the only way that produces evidence rather than an opinion.
#
# The install is attempted; whatever happens is then classified. "Refused" is a first-class pass here -
# the roadmap asks for supported *or* refused, and a shape HookLab cannot honestly intercept should say
# so. What fails is silence: an install that reports success while the observed value never moves.
function Test-Boundary([string]$Name,[string]$Type,[string]$Method,[string]$HookId,[int]$Delta) {
	Write-Host ""
	Write-Host "-- $Name" -ForegroundColor Cyan
	$members = Rpc 'list_members' @{ session_id=$sessionId; module=$moduleName; module_id=$moduleId; type=$Type; name_pattern=$Method }
	# Exact, not -like: 'Work' matches 'GenericWork' too, and a boundary gate that silently measured the
	# wrong method would report the generic answer for the control case and look consistent doing it.
	$member = @($members.symbols | Where-Object { $_.name -eq $Method } | Select-Object -First 1)[0]
	if (-not $member) {
		Check "$Name is resolvable at all" $false ("list_members returned: " + ($members | ConvertTo-Json -Compress -Depth 4))
		return
	}
	Write-Host ("   token=" + $member.method_token + " name=" + $member.name)

	$template = $null
	try { $template = Rpc 'get_hook_template' @{ session_id=$sessionId; module_id=$moduleId; method_token=[int]$member.method_token; template='Postfix' } }
	catch {
		Check "$Name gives a decisive answer" $true ("refused at get_hook_template: " + $_.Exception.Message)
		Write-Host ("   REFUSED (template): " + $_.Exception.Message) -ForegroundColor Yellow
		return
	}

	$baseline = Behavior
	$hook = @{
		session_id=$sessionId; process_id=$processId; hook_id=$HookId; kind='Postfix'
		module_id=$moduleId; assembly='MonoHookLabTarget'; declaring_type=$template.target.declaring_type
		method=$template.target.method; method_token=$template.target.method_token; signature=$template.target.signature
		module_mvid=$template.target.module_mvid; il_sha256=$template.target.il_sha256; revision=1
		# Adds rather than replaces, so the expected total is baseline+delta whatever the method
		# contributed before. A replacing postfix makes the expectation depend on which method is under
		# test, and a gate whose arithmetic differs per case reports its own bug as a product failure.
		source=('public static class ' + $HookId.Replace('-','') + 'Postfix { public static void Postfix(ref int __result) { __result = __result + ' + $Delta + '; } }')
	}
	try { $created = Rpc 'create_hook' $hook 120 }
	catch {
		Check "$Name gives a decisive answer" $true ("refused at create_hook: " + $_.Exception.Message)
		Write-Host ("   REFUSED (install): " + $_.Exception.Message) -ForegroundColor Yellow
		return
	}

	# Installed. Now the only question that matters: did anything about the target actually change?
	$expected = $baseline + $Delta
	$observed = Wait-Until { (Behavior) -eq $expected } 20
	if ($observed) {
		Write-Host ("   SUPPORTED: behaviour moved " + $baseline + " -> " + $expected) -ForegroundColor Green
		Check "$Name gives a decisive answer" $true
	}
	else {
		Write-Host ("   SILENT: installed=" + $created.installed + " but behaviour stayed at " + (Behavior) + " (expected " + $expected + ")") -ForegroundColor Red
		Check "$Name gives a decisive answer" $false "installed successfully but intercepted nothing, and said nothing about it"
	}
	try { $null = Rpc 'remove_hook' @{ session_id=$sessionId; process_id=$processId; hook_id=$HookId } } catch { }
	$null = Wait-Until { (Behavior) -eq $baseline } 20
}

try {
	if ([string]::IsNullOrWhiteSpace($MonoExe)) { throw 'This gate needs a Mono runtime. Pass -MonoExe or set DGSPY_MONO_EXE; dgSpy does not ship or discover one.' }
	if (-not (Test-Path -LiteralPath $MonoExe)) { throw "No Mono runtime at $MonoExe." }
	if (-not (Test-Path -LiteralPath $targetExe)) { throw "The Mono HookLab fixture is not built: $targetExe" }
	New-Item -ItemType Directory -Force -Path $root | Out-Null
	Start-Transcript -LiteralPath (Join-Path $root 'driver.log') -Force | Out-Null

	if ($AgentPort -eq 0) {
		$probe = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0)
		$probe.Start(); $AgentPort = $probe.LocalEndpoint.Port; $probe.Stop()
	}
	$agent = "--debugger-agent=transport=dt_socket,server=y,address=127.0.0.1:$AgentPort,suspend=n"
	$arguments = if ($MonoRuntime) { @(('"'+$MonoRuntime+'"'),$agent,('"'+$targetExe+'"'),('"'+$root+'"')) }
		else { @($agent,('"'+$targetExe+'"'),('"'+$root+'"')) }
	if ($MonoAssemblies) { $env:MONO_PATH = $MonoAssemblies }
	$target = Start-Process -FilePath $MonoExe -ArgumentList $arguments `
		-WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $root 'target.out') -RedirectStandardError (Join-Path $root 'target.err')
	$null = $target.Handle
	if (-not (Wait-Until { Test-Path -LiteralPath (Join-Path $root 'ready.txt') } 30)) { throw 'The Mono fixture did not report ready within 30 seconds.' }
	Check 'the fixture ticks its unhooked value' (Wait-Until { (Behavior) -eq 42 } 15) ("behavior=" + (Behavior))
	Check "the Mono agent is listening on $AgentPort" (Wait-Until { @(Get-NetTCPConnection -State Listen -LocalPort $AgentPort -ErrorAction SilentlyContinue).Count -gt 0 } 30)

	$hostId = & (Join-Path $PSScriptRoot 'TestSupport\Start-DgSpyHost.ps1') -RpcPort $RpcPort -TargetFramework $TargetFramework
	$RpcPort = [int]$env:DGSPY_RPC_PORT

	$attached = Rpc 'attach_endpoint' @{ address='127.0.0.1'; port=$AgentPort; engine='mono'; process_is_suspended=$false; connection_timeout_ms=30000 }
	Check 'attach_endpoint opens a Mono session' ($attached.state -ne 'faulted') ("state=" + $attached.state + " " + $attached.fault_message)
	$sessionId = $attached.session_id
	$processId = @($attached.process_ids)[0]

	$modules = @((Rpc 'list_modules' @{ session_id=$sessionId; name_pattern='MonoHookLabTarget' }).modules)
	Check 'the fixture module is discovered' ($modules.Count -ge 1)
	$moduleId = $modules[0].module_id
	$moduleName = $modules[0].name

	# Arrival, exactly as the smoke gate does it: stop on a real frame, clear, initialize, resume.
	$breakpoint = Rpc 'set_breakpoint' @{ session_id=$sessionId; module=$moduleName; module_id=$moduleId; type='MonoHookLabTarget.Program'; method='Tick' }
	Check 'a breakpoint binds in the Mono target' ($breakpoint.bound_count -gt 0)
	$stop = Rpc 'wait_for_stop' @{ session_id=$sessionId; after_event_id=$breakpoint.cursor_event_id; timeout_ms=30000 }
	Check 'the loop reaches the carrier method' (-not $stop.timed_out)
	$null = Rpc 'clear_breakpoints' @{ session_id=$sessionId }
	$initialized = Rpc 'initialize_hooklab' @{ session_id=$sessionId; process_id=$processId } 120
	Check 'the resident arrives' ($initialized.initialized -eq $true) ($initialized | ConvertTo-Json -Compress -Depth 4)
	if (-not (Rpc 'get_session_state' @{ session_id=$sessionId }).is_running) { $null = Rpc 'continue' @{ session_id=$sessionId } }

	# The control case. If this one is not decisive the gate is measuring itself, not the boundaries.
	Test-Boundary 'a plain static method' 'MonoHookLabTarget.Program' 'Work' 'boundary-plain' 100

	# Boundary 1: a generic method definition. There is no single runtime method to patch until it is
	# closed over a type argument, and the guards - token, signature, IL digest - describe the
	# definition rather than any instantiation of it.
	Test-Boundary 'a generic method' 'MonoHookLabTarget.Program' 'GenericWork' 'boundary-generic-method' 200

	# Boundary 2: a non-generic method on a generic declaring type. Same problem, one level out.
	# The exact spelling of a nested generic type name is the debugger's to state, not this gate's to
	# guess, so it is looked up rather than hardcoded.
	$holder = @((Rpc 'list_types' @{ session_id=$sessionId; module=$moduleName; module_id=$moduleId; name_pattern='Holder' }).symbols | Select-Object -First 1)[0]
	if ($holder) {
		Write-Host ("generic type resolved as: " + $holder.name)
		Test-Boundary 'a method on a generic type' $holder.name 'Work' 'boundary-generic-type' 300
	}
	else { Check 'the generic declaring type is discoverable' $false 'list_types found no Holder' }

	# Boundary 3: a method the JIT is asked to inline. Patching it cannot affect call sites already
	# compiled, so an install can succeed while nothing is intercepted.
	Test-Boundary 'an aggressively inlined method' 'MonoHookLabTarget.Program' 'Inlineable' 'boundary-inlined' 400

	# The refusal above came from get_hook_template, which is an authoring convenience. It says nothing
	# about install_hook/create_hook, which take every identity fact as an argument and can be called
	# without ever asking for a template - by an exported hook package, or by hand.
	#
	# So the same generic method is offered directly, with guards derived offline by reflection over the
	# assembly on disk exactly as run-mono-hooklab-smoke.ps1 derives its own. If only the template
	# refuses, a generic carrier reaches Harmony through the front door and whatever happens there is
	# what an operator gets.
	Write-Host ""
	Write-Host "-- a generic method offered directly to create_hook" -ForegroundColor Cyan
	$facts = Get-MethodFacts $targetExe 'MonoHookLabTarget.Program' 'GenericWork'
	Write-Host ("   offline facts token=" + $facts.Token + " signature='" + $facts.Signature + "'")
	$direct = @{
		session_id=$sessionId; process_id=$processId; hook_id='boundary-generic-direct'; kind='Postfix'
		module_id=$moduleId; assembly='MonoHookLabTarget'; declaring_type='MonoHookLabTarget.Program'
		method='GenericWork'; method_token=[int]$facts.Token; signature=$facts.Signature
		module_mvid=$facts.Mvid; il_sha256=$facts.IlSha256; revision=1
		source='public static class BoundaryGenericDirect { public static void Postfix(ref int __result) { __result = __result + 500; } }'
	}
	$baseline = Behavior
	$refusal = $null
	try { $installed = Rpc 'create_hook' $direct 120 }
	catch { $refusal = $_.Exception.Message }
	if ($refusal) {
		Write-Host ("   REFUSED: " + $refusal) -ForegroundColor Yellow
		Check 'a generic carrier is refused by create_hook, not only by the template' `
			($refusal -match 'generic') "the refusal did not name the shape: $refusal"
	}
	else {
		$moved = Wait-Until { (Behavior) -ne $baseline } 15
		Write-Host ("   INSTALLED: installed=" + $installed.installed + " behaviour " + $baseline + " -> " + (Behavior)) -ForegroundColor Red
		Check 'a generic carrier is refused by create_hook, not only by the template' $false `
			("create_hook accepted a generic method; behaviour moved=" + $moved)
		try { $null = Rpc 'remove_hook' @{ session_id=$sessionId; process_id=$processId; hook_id='boundary-generic-direct' } } catch { }
	}

	# The session-lifetime boundary, and a different kind of question from the shapes above: a resident
	# outlives the debugger session that installed it, so what happens when the soft-debugger connection
	# goes away and a new one arrives has to be a stated result rather than an assumption. Preserving a
	# foreign or orphaned hook is an invariant this product already claims; adoption is how it keeps it.
	Write-Host ""
	Write-Host "-- reconnect and adoption" -ForegroundColor Cyan
	$adoptHook = @{
		session_id=$sessionId; process_id=$processId; hook_id='boundary-adopt'; kind='Postfix'
		module_id=$moduleId; assembly='MonoHookLabTarget'; declaring_type='MonoHookLabTarget.Program'
		method='Work'; method_token=[int](Get-MethodFacts $targetExe 'MonoHookLabTarget.Program' 'Work').Token
		signature=(Get-MethodFacts $targetExe 'MonoHookLabTarget.Program' 'Work').Signature
		module_mvid=(Get-MethodFacts $targetExe 'MonoHookLabTarget.Program' 'Work').Mvid
		il_sha256=(Get-MethodFacts $targetExe 'MonoHookLabTarget.Program' 'Work').IlSha256; revision=1
		source='public static class BoundaryAdopt { public static void Postfix(ref int __result) { __result = __result + 600; } }'
	}
	$null = Rpc 'create_hook' $adoptHook 120
	Check 'a hook is installed before the connection drops' (Wait-Until { (Behavior) -eq 642 } 20) ("behavior=" + (Behavior))

	$null = Rpc 'detach' @{ session_id=$sessionId } 30
	$sessionId = $null
	Check 'the hook keeps working with no debugger attached' (Wait-Until { (Behavior) -eq 642 } 10) ("behavior=" + (Behavior))

	# A second connection to the same agent. Whether Mono's soft debugger accepts one after a detach is
	# itself part of the answer, so a refusal here is reported rather than thrown past.
	$reattached = $null
	try { $reattached = Rpc 'attach_endpoint' @{ address='127.0.0.1'; port=$AgentPort; engine='mono'; process_is_suspended=$false; connection_timeout_ms=30000 } }
	catch { Check 'the Mono agent accepts a second debugger session' $false $_.Exception.Message }
	if ($reattached -and $reattached.state -ne 'faulted') {
		Check 'the Mono agent accepts a second debugger session' $true
		$sessionId = $reattached.session_id
		$processId = @($reattached.process_ids)[0]
		$readopted = Rpc 'initialize_hooklab' @{ session_id=$sessionId; process_id=$processId } 120
		Write-Host ("   initialize: " + ($readopted | ConvertTo-Json -Depth 4 -Compress))
		# Adopted, not installed again: byte-loading a second generation into the same AppDomain is the
		# one thing arrival must never do, and it cannot be undone if it happens.
		Check 'the existing resident is adopted rather than replaced' `
			($readopted.initialized -eq $true -and $readopted.adopted -eq $true) ($readopted | ConvertTo-Json -Compress -Depth 4)
		$surviving = @((Rpc 'list_hooks' @{ session_id=$sessionId; process_id=$processId }).hooks)
		Check 'the hook installed by the previous session survives the reconnect' `
			(@($surviving | Where-Object { $_.hook_id -eq 'boundary-adopt' }).Count -eq 1) (($surviving | ConvertTo-Json -Compress -Depth 4))
		$null = Rpc 'remove_hook' @{ session_id=$sessionId; process_id=$processId; hook_id='boundary-adopt' }
		Check 'the adopted hook can be removed by the new session' (Wait-Until { (Behavior) -eq 42 } 20) ("behavior=" + (Behavior))
		$null = Rpc 'detach' @{ session_id=$sessionId } 30
		$sessionId = $null
	}
	elseif ($reattached) { Check 'the Mono agent accepts a second debugger session' $false ("state=" + $reattached.state + " " + $reattached.fault_message) }

	Check 'the Mono target survives the boundary sweep' (-not $target.HasExited)
}
catch {
	$script:fail++
	Write-Host ("FAIL  the gate did not finish: " + $_.Exception.Message) -ForegroundColor Red
	Write-Host ($_.ScriptStackTrace)
}
finally {
	if ($sessionId) { try { $null = Rpc 'detach' @{ session_id=$sessionId } 30 } catch { } }
	try { Set-Content -LiteralPath (Join-Path $root 'stop.txt') -Value 'stop' } catch { }
	if ($target -and -not $target.HasExited) { Start-Sleep -Seconds 2 }
	if ($target -and -not $target.HasExited) { Stop-Process -Id $target.Id -Force -ErrorAction SilentlyContinue }
	if ($hostId -gt 0) { Stop-Process -Id $hostId -Force -ErrorAction SilentlyContinue }
	Write-Host ""
	Write-Host "Mono HookLab boundaries: $pass passed, $fail failed" -ForegroundColor Cyan
	Write-Host "logs: $root"
	try { Stop-Transcript | Out-Null } catch { }
}
if ($fail -ne 0) { exit 1 }
exit 0
