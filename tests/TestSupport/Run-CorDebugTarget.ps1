$ErrorActionPreference = 'Stop'

$targetExe = $env:DGSPY_CORDEBUG_TARGET_EXE
$stdoutPath = $env:DGSPY_CORDEBUG_TARGET_OUT
$stderrPath = $env:DGSPY_CORDEBUG_TARGET_ERR
if ([string]::IsNullOrWhiteSpace($targetExe)) { throw 'DGSPY_CORDEBUG_TARGET_EXE is required.' }
if ([string]::IsNullOrWhiteSpace($stdoutPath)) { throw 'DGSPY_CORDEBUG_TARGET_OUT is required.' }
if ([string]::IsNullOrWhiteSpace($stderrPath)) { throw 'DGSPY_CORDEBUG_TARGET_ERR is required.' }

# This process owns the redirection. The hidden-desktop parent only holds this launcher's process
# handle, so Windows PowerShell cannot wait on a redirected pipe that the long-running target keeps open.
& $targetExe 1> $stdoutPath 2> $stderrPath
exit $LASTEXITCODE
