[CmdletBinding()]
param(
    [ValidateSet('net48')][string]$TargetFramework = 'net48',
    [int]$RpcPort = 7362,
    [string]$RunDirectory,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new()
$repoRoot = Split-Path $PSScriptRoot -Parent
$logRoot = if ([string]::IsNullOrWhiteSpace($env:DGSPY_SMOKE_LOG_ROOT)) { Join-Path $PSScriptRoot 'artifacts' } else { $env:DGSPY_SMOKE_LOG_ROOT }
if ([string]::IsNullOrWhiteSpace($RunDirectory)) { $RunDirectory = Join-Path $logRoot ('dgspy-smoke-owned-breakpoints-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
$dnSpyDir = Join-Path $repoRoot 'dnSpy\dnSpy\bin\Release\net48'
$targetExe = Join-Path $repoRoot 'tests\TestTargets\Milestone1Target\bin\Release\net48\Milestone1Target.exe'
$controllerProject = Join-Path $PSScriptRoot 'OwnedBreakpoint.LiveTestExtension\OwnedBreakpoint.LiveTestExtension.csproj'
$controllerDeploy = Join-Path $dnSpyDir 'bin\Extensions\dgSpy'
$controllerOutput = Join-Path $PSScriptRoot 'OwnedBreakpoint.LiveTestExtension\bin\Release\net48'
$controllerLog = Join-Path $RunDirectory 'controller.log'
$commands = Join-Path $RunDirectory 'commands.txt'
$driverLog = Join-Path $RunDirectory 'driver.log'
$targetLauncher = $null
$targetId = 0
$hostProcess = $null
$pass = 0
$fail = 0

. (Join-Path $PSScriptRoot 'TestSupport\Invoke-DgSpyRpc.ps1')

function Say([string]$Text) {
    $line = (Get-Date -Format 'HH:mm:ss') + '  ' + $Text
    Write-Host $line
    Add-Content -LiteralPath $driverLog -Value $line -Encoding utf8
}
function Check([string]$Name, [bool]$Ok, [string]$Detail = '') {
    if ($Ok) { $script:pass++; Say ('PASS  ' + $Name + $(if ($Detail) { '  ' + $Detail } else { '' })) }
    else { $script:fail++; Say ('FAIL  ' + $Name + '  ' + $Detail) }
}
function Rpc([string]$Operation, [hashtable]$Arguments = @{}, [int]$Deadline = 20) {
    Invoke-DgSpyRpc -OperationName $Operation -OperationArguments $Arguments -RpcPort $RpcPort -DeadlineSeconds $Deadline
}
function Controller([string]$Command, [string]$Operation, [int]$TimeoutSeconds = 20) {
    $before = if (Test-Path $controllerLog) { @(Get-Content $controllerLog).Count } else { 0 }
    Add-Content -LiteralPath $commands -Value $Command -Encoding ascii
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $lines = if (Test-Path $controllerLog) { @(Get-Content $controllerLog) } else { @() }
        if ($lines.Count -gt $before) {
            $new = @($lines[$before..($lines.Count - 1)])
            $terminal = $new | Where-Object { $_ -match ("`t" + [regex]::Escape($Operation) + "`t(ok|error)`t") } | Select-Object -Last 1
            if ($terminal) {
                if ($terminal -match "`terror`t") { throw "controller command '$Command' failed: $terminal" }
                return ,$new
            }
        }
        Start-Sleep -Milliseconds 50
    }
    throw "controller command '$Command' timed out"
}
function Detail([string[]]$Lines, [string]$Operation, [string]$Status = 'ok') {
    $line = $Lines | Where-Object { $_ -match ("`t" + $Operation + "`t" + $Status + "`t") } | Select-Object -Last 1
    if (-not $line) { return '' }
    ($line -split "`t",4)[3]
}
function Value([string]$Text, [string]$Name) {
    $match = [regex]::Match($Text, '(?:^| )' + [regex]::Escape($Name) + '=(.*?)(?= \w+=|$)')
    if ($match.Success) { $match.Groups[1].Value } else { $null }
}
function UserSettings([int]$Id, [string]$Tag) {
    $detail = Detail (Controller "snapshot $Id $Tag" 'snapshot') 'snapshot'
    $names = @('enabled','cond','trace','hitcount','filter','labels')
    ($names | ForEach-Object { $_ + '=' + (Value $detail $_) }) -join ' '
}
function OwnerInfo {
    $lines = Controller 'owners' 'owners'
    $items = @($lines | Where-Object { $_ -match "`towners`titem`t" })
    $summary = Detail $lines 'owners'
    [pscustomobject]@{ Items=$items; Count=[int](Value $summary 'count'); ServiceCount=[int](Value $summary 'service_count') }
}
function Physical([uint32]$Token, [uint32]$Offset = 0) {
    $detail = Detail (Controller "physical $Token $Offset" 'physical') 'physical'
    [int](Value $detail 'count')
}
function Ensure-Paused([string]$SessionId) {
    $state = Rpc 'get_session_state' @{ session_id=$SessionId }
    if ($state.state -ne 'paused') { $null = Rpc 'pause' @{ session_id=$SessionId } }
}
function EngineHits([int]$Id) {
    [long](@(Rpc 'list_breakpoints' @{} | Where-Object { $_.breakpoint_id -eq $Id }).engine_hit_count)
}

New-Item -ItemType Directory -Force -Path $RunDirectory | Out-Null
try {
    if (-not $SkipBuild) {
        Say 'building and deploying production net48 extension'
        $msbuild = 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\amd64\MSBuild.exe'
        if (-not (Test-Path -LiteralPath $msbuild)) { throw "required net48 MSBuild not found: $msbuild" }
        & (Join-Path $repoRoot 'build.ps1') netframework -MSBuildPath $msbuild
        if ($LASTEXITCODE) { throw "net48 host build failed with exit code $LASTEXITCODE" }
        & (Join-Path $repoRoot 'build-dgspy.ps1') -TargetFramework $TargetFramework
        if ($LASTEXITCODE) { throw "build-dgspy failed with exit code $LASTEXITCODE" }
    }
    Say 'building and deploying the live-test controller'
    # The packaged host is already built. Rebuilding dnSpy ProjectReferences here can copy their
    # dependency closure into the host root and make dnSpy stop scanning bin\Extensions entirely.
    & dotnet build $controllerProject -c Release --nologo -v:minimal -p:BuildProjectReferences=false
    if ($LASTEXITCODE) { throw "controller build failed with exit code $LASTEXITCODE" }
    & (Join-Path $repoRoot 'tools\Test-DgSpyPackagedHostLayout.ps1') -HostRoot $dnSpyDir -TargetFramework $TargetFramework
    New-Item -ItemType Directory -Force -Path $controllerDeploy | Out-Null
    Copy-Item (Join-Path $controllerOutput 'OwnedBreakpoint.LiveTestExtension.x.dll') $controllerDeploy -Force
    Copy-Item (Join-Path $controllerOutput 'OwnedBreakpoint.LiveTestExtension.x.pdb') $controllerDeploy -Force

    $env:DGSPY_RPC_PORT = [string]$RpcPort
    $env:DGSPY_OWNED_BP_TEST_DIR = $RunDirectory
    if (@(Get-NetTCPConnection -State Listen -LocalPort $RpcPort -ErrorAction SilentlyContinue).Count) { throw "port $RpcPort is already in use" }

    $targetOut = Join-Path $RunDirectory 'target.out'
    $targetErr = Join-Path $RunDirectory 'target.err'
    $env:DGSPY_OWNED_BP_TARGET_EXE = $targetExe
    $env:DGSPY_OWNED_BP_TARGET_OUT = $targetOut
    $env:DGSPY_OWNED_BP_TARGET_ERR = $targetErr
    $targetLauncherScript = Join-Path $PSScriptRoot 'OwnedBreakpoint.LiveTestExtension\Run-TestTarget.ps1'
    $targetLauncher = Start-Process -FilePath "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" `
        -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-File',('"' + $targetLauncherScript + '"') -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    while ([DateTime]::UtcNow -lt $deadline -and @(Get-Content -LiteralPath $targetOut -ErrorAction SilentlyContinue).Count -lt 2) {
        if ($targetLauncher.HasExited) { break }
        Start-Sleep -Milliseconds 100
    }
    $targetLines = @(Get-Content -LiteralPath $targetOut -ErrorAction SilentlyContinue)
    $pidLine = $targetLines | Where-Object { $_ -match '^PID=\d+$' } | Select-Object -First 1
    $tokenLine = $targetLines | Where-Object { $_ -match '^TOKEN=\d+$' } | Select-Object -First 1
    if (-not $pidLine -or -not $tokenLine) {
        $stderrTail = @(Get-Content -LiteralPath $targetErr -Tail 20 -ErrorAction SilentlyContinue) -join ' | '
        throw "target did not publish PID and token; stderr=$stderrTail"
    }
    $targetId = [int]($pidLine -replace '^PID=', '')
    $token = [uint32]($tokenLine -replace '^TOKEN=', '')

    for ($attempt=1; $attempt -le 3 -and -not (Test-Path $controllerLog); $attempt++) {
        $hostProcess = Start-Process -FilePath (Join-Path $dnSpyDir 'dnSpy.exe') -WorkingDirectory $dnSpyDir -ArgumentList '--multiple','--dgspy-no-window-activation' -PassThru
        $deadline = [DateTime]::UtcNow.AddSeconds(90)
        while ([DateTime]::UtcNow -lt $deadline -and -not @(Get-NetTCPConnection -State Listen -LocalPort $RpcPort -ErrorAction SilentlyContinue).Count) {
            if ($hostProcess.HasExited) { break }
            Start-Sleep -Milliseconds 250
        }
        $deadline = [DateTime]::UtcNow.AddSeconds(20)
        while ([DateTime]::UtcNow -lt $deadline -and -not (Test-Path $controllerLog) -and -not $hostProcess.HasExited) { Start-Sleep -Milliseconds 100 }
        if (-not (Test-Path $controllerLog) -and -not $hostProcess.HasExited) {
            # dnSpy can rebuild its MEF extension cache without activating a newly changed extension
            # until the next process. This host has not attached yet, so ending only this test host is safe.
            $hostProcess.Kill()
            $hostProcess.WaitForExit(10000) | Out-Null
            Start-Sleep -Seconds 2
        }
    }
    Check 'the production-service controller composed' (Test-Path $controllerLog) $controllerLog
    if (-not (Test-Path $controllerLog)) { throw 'the live-test controller did not compose' }
    $prefix = Detail @((Get-Content $controllerLog | Select-Object -First 1)) 'loaded'
    Check 'T07 exposes the T08 shared owned-breakpoint vocabulary' ($prefix -eq 'owned_breakpoint:') $prefix

    $program = @(Rpc 'list_programs' @{ process_ids=@($targetId) })[0]
    $session = Rpc 'attach' @{ program_id=$program.program_id } 40
    $sessionId = $session.session_id
    $user = Rpc 'set_il_breakpoint' @{ session_id=$sessionId; module=$targetExe; method_token=$token; il_offset=0 }
    $userId = [int]$user.breakpoint_id
    $elsewhere = Rpc 'set_breakpoint' @{ session_id=$sessionId; module='Milestone1Target.exe'; type='Milestone1Target.Program'; method='Main' }
    $elsewhereId = [int]$elsewhere.breakpoint_id
    $null = Controller "labels $userId t07-user-label" 'labels'
    $elsewhereBefore = @((Rpc 'list_breakpoints' @{}) | Where-Object { $_.breakpoint_id -eq $elsewhereId })[0]

    function Run-MatrixCase {
        param([string]$Name,[hashtable]$Settings,[bool]$UserBound,[bool]$UserStops,[int]$RunMilliseconds=1200)
        Ensure-Paused $sessionId
        if ($Settings.Count) { $null = Rpc 'update_breakpoint' (@{ breakpoint_id=$userId } + $Settings) }
        $before = UserSettings $userId ('before-' + $Name)
        $add = Detail (Controller "add $userId resume" 'add') 'add'
        $ownerToken = Value $add 'token'
        Check "[$Name] owner bound" ((Value $add 'state') -eq 'Bound') $add
        $afterAdd = UserSettings $userId ('after-add-' + $Name)
        Check "[$Name] user settings unchanged on add" ($before -eq $afterAdd) $afterAdd
        Check "[$Name] physical owner count" ((Physical $token) -eq $(if($UserBound){2}else{1})) ("count=" + (Physical $token))
        $ownerBefore = OwnerInfo
        $hitsBefore = [long](Value (($ownerBefore.Items[0] -split "`t",4)[3]) 'hits')
        $engineBefore = EngineHits $userId
        $cursor = (Rpc 'get_session_state' @{ session_id=$sessionId }).last_event_id
        $null = Rpc 'continue' @{ session_id=$sessionId }
        $stop = Rpc 'wait_for_stop' @{ session_id=$sessionId; after_event_id=$cursor; timeout_ms=$RunMilliseconds } 20
        if ($stop.timed_out) { $null = Rpc 'pause' @{ session_id=$sessionId } }
        $ownerAfter = OwnerInfo
        $hitsAfter = [long](Value (($ownerAfter.Items[0] -split "`t",4)[3]) 'hits')
        $engineAfter = EngineHits $userId
        Check "[$Name] internal pipeline received hit" ($hitsAfter -gt $hitsBefore) ("hits $hitsBefore -> $hitsAfter")
        Check "[$Name] user pipeline independence" ($(if($UserBound){$engineAfter -gt $engineBefore}else{$engineAfter -eq $engineBefore})) ("engine $engineBefore -> $engineAfter")
        if ($UserStops) {
            $event = @($stop.events)[0]
            Check "[$Name] user-visible stop wins" (-not $stop.timed_out -and $event.stop_reason -eq 'breakpoint' -and $event.breakpoint_id -eq $userId) ("reason="+$event.stop_reason)
        }
        else { Check "[$Name] user did not stop" ($stop.timed_out) ("timed_out="+$stop.timed_out) }
        Ensure-Paused $sessionId
        $release = Detail (Controller "release $ownerToken" 'release') 'release'
        Check "[$Name] release settled" ((Value $release 'remaining') -eq '0') $release
        $afterRelease = UserSettings $userId ('after-release-' + $Name)
        Check "[$Name] user settings unchanged on release" ($before -eq $afterRelease) $afterRelease
        Check "[$Name] only owner physical removed" ((Physical $token) -eq $(if($UserBound){1}else{0})) ("count=" + (Physical $token))
        Check "[$Name] elsewhere breakpoint untouched" (@((Rpc 'list_breakpoints' @{}) | Where-Object { $_.breakpoint_id -eq $elsewhereId }).Count -eq 1) ("id="+$elsewhereId)
    }

    Run-MatrixCase 'enabled' @{ enabled=$true; condition=''; trace_message='' } $true $true
    Run-MatrixCase 'conditional-true' @{ enabled=$true; condition='input == 41'; condition_kind='is_true'; trace_message='' } $true $true
    Run-MatrixCase 'conditional-false' @{ enabled=$true; condition='input == 999'; condition_kind='is_true'; trace_message='' } $true $false
    Run-MatrixCase 'disabled' @{ enabled=$false; condition=''; trace_message='' } $false $false
    Run-MatrixCase 'tracepoint' @{ enabled=$true; condition=''; trace_message='t07-trace {input}'; trace_continue=$true } $true $false

    Say 're-enable race'
    Ensure-Paused $sessionId
    $null = Rpc 'update_breakpoint' @{ breakpoint_id=$userId; enabled=$false; condition=''; trace_message='' }
    $raceToken = Value (Detail (Controller "add $userId resume" 'add') 'add') 'token'
    $null = Rpc 'continue' @{ session_id=$sessionId }
    Start-Sleep -Milliseconds 300
    $null = Rpc 'update_breakpoint' @{ breakpoint_id=$userId; enabled=$true }
    $cursor = (Rpc 'get_session_state' @{ session_id=$sessionId }).last_event_id
    $raceStop = Rpc 'wait_for_stop' @{ session_id=$sessionId; after_event_id=$cursor; timeout_ms=5000 } 20
    Check 're-enabled user breakpoint inserts beside a running owner and wins visibly' (-not $raceStop.timed_out -and @($raceStop.events)[0].breakpoint_id -eq $userId) ("physical="+(Physical $token))
    Check 'both physical owners exist after re-enable' ((Physical $token) -eq 2) ("count="+(Physical $token))
    $null = Controller "release $raceToken" 'release'

    Say 'internal-only stop attribution'
    $null = Rpc 'update_breakpoint' @{ breakpoint_id=$userId; enabled=$false }
    $stopToken = Value (Detail (Controller "add $userId stop" 'add') 'add') 'token'
    $cursor = (Rpc 'get_session_state' @{ session_id=$sessionId }).last_event_id
    $null = Rpc 'continue' @{ session_id=$sessionId }
    $ownedStop = Rpc 'wait_for_stop' @{ session_id=$sessionId; after_event_id=$cursor; timeout_ms=5000 } 20
    $ownedEvent = @($ownedStop.events)[0]
    Check 'internal-only stop is labeled with its owner' ($ownedEvent.reason -eq ('owned_breakpoint:'+$stopToken)) ("reason="+$ownedEvent.reason)
    $null = Controller "release $stopToken" 'release'

    Say 'user-first removal order'
    $null = Rpc 'update_breakpoint' @{ breakpoint_id=$userId; enabled=$true }
    $userFirstToken = Value (Detail (Controller "add $userId resume" 'add') 'add') 'token'
    $null = Rpc 'remove_breakpoint' @{ breakpoint_id=$userId }
    Check 'removing the user first preserves the internal physical owner' ((Physical $token) -eq 1) ("count="+(Physical $token))
    $beforeHits = [long](Value ((((OwnerInfo).Items[0] -split "`t",4)[3])) 'hits')
    $null = Rpc 'continue' @{ session_id=$sessionId }
    Start-Sleep -Milliseconds 700
    $null = Rpc 'pause' @{ session_id=$sessionId }
    $afterHits = [long](Value ((((OwnerInfo).Items[0] -split "`t",4)[3])) 'hits')
    Check 'internal owner still receives hits after user removal' ($afterHits -gt $beforeHits) ("hits $beforeHits -> $afterHits")
    $null = Controller "release $userFirstToken" 'release'
    Check 'last owner removal clears the physical breakpoint' ((Physical $token) -eq 0) ("count="+(Physical $token))

    Say 'crashed-owner reclamation and settle-before-terminal'
    $user = Rpc 'set_il_breakpoint' @{ session_id=$sessionId; module=$targetExe; method_token=$token; il_offset=0 }
    $userId = [int]$user.breakpoint_id
    $null = Rpc 'update_breakpoint' @{ breakpoint_id=$userId; enabled=$false }
    $null = Controller "add $userId resume" 'add'
    $reclaim = Detail (Controller 'reclaim' 'reclaim') 'reclaim'
    Check 'an abandoned owner is reclaimable' ((Value $reclaim 'count') -eq '1' -and (Value $reclaim 'remaining') -eq '0') $reclaim
    Check 'settling completed before the reclaim command returned terminally' ((Physical $token) -eq 0) ("count="+(Physical $token))
    Check 'the unrelated breakpoint survived every path' (@((Rpc 'list_breakpoints' @{}) | Where-Object { $_.breakpoint_id -eq $elsewhereId }).Count -eq 1) ("id="+$elsewhereId)

    Ensure-Paused $sessionId
    $null = Rpc 'detach' @{ session_id=$sessionId } 40
}
finally {
    try {
        $targetProcess = if ($targetId) { Get-Process -Id $targetId -ErrorAction SilentlyContinue } else { $null }
        if ($targetProcess) {
            $targetProcess.Kill()
            $targetProcess.WaitForExit(10000) | Out-Null
        }
    } catch { }
    try {
        if ($targetLauncher -and -not $targetLauncher.HasExited) { $targetLauncher.Kill() }
        if ($targetLauncher) { $targetLauncher.WaitForExit(10000) | Out-Null }
    } catch { }
    try {
        if ($hostProcess -and -not $hostProcess.HasExited) { $hostProcess.Kill() }
        if ($hostProcess) { $hostProcess.WaitForExit(10000) | Out-Null }
    } catch { }
    foreach ($name in 'OwnedBreakpoint.LiveTestExtension.x.dll','OwnedBreakpoint.LiveTestExtension.x.pdb') {
        $deployedController = Join-Path $controllerDeploy $name
        Remove-Item -LiteralPath $deployedController -Force -ErrorAction SilentlyContinue
        if (Test-Path -LiteralPath $deployedController) { throw "failed to remove live-test controller: $deployedController" }
    }
    Remove-Item Env:DGSPY_OWNED_BP_TEST_DIR -ErrorAction SilentlyContinue
    Remove-Item Env:DGSPY_RPC_PORT -ErrorAction SilentlyContinue
    Remove-Item Env:DGSPY_OWNED_BP_TARGET_EXE -ErrorAction SilentlyContinue
    Remove-Item Env:DGSPY_OWNED_BP_TARGET_OUT -ErrorAction SilentlyContinue
    Remove-Item Env:DGSPY_OWNED_BP_TARGET_ERR -ErrorAction SilentlyContinue
    Say "passed=$pass failed=$fail"
    Say "artifacts=$RunDirectory"
}
if ($fail) { exit 1 }
