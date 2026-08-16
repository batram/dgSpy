#requires -Version 5.1

# A freshly started target can briefly be invisible to the attach providers while its runtime
# finishes initializing, so a single-shot list_programs probe right after process start is a race.
# Every smoke that discovers its own fixture goes through this bounded retry instead.
function Find-DgSpyProgram {
	[CmdletBinding()]
	param(
		[Parameter(Mandatory=$true)][int]$ProcessId,
		[Parameter(Mandatory=$true)][scriptblock]$InvokeRpc,
		[int]$TimeoutSeconds=10,
		[int]$RetryDelayMilliseconds=250
	)
	$deadline=[DateTime]::UtcNow.AddSeconds($TimeoutSeconds); $attempts=0
	do {
		$attempts++
		$program=@(& $InvokeRpc 'list_programs' @{process_ids=@($ProcessId)})[0]
		if($program){return $program}
		if(-not (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue)){throw "target pid $ProcessId exited before it became discoverable (after $attempts list_programs attempts)"}
		Start-Sleep -Milliseconds $RetryDelayMilliseconds
	} while([DateTime]::UtcNow -lt $deadline)
	throw "target pid $ProcessId was not discoverable within ${TimeoutSeconds}s ($attempts list_programs attempts; the process is still alive)"
}
