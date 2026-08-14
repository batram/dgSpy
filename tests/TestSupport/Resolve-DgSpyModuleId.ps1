#requires -Version 5.1

function Resolve-DgSpyModuleId {
	[CmdletBinding()]
	param(
		[Parameter(Mandatory=$true)][string]$SessionId,
		[Parameter(Mandatory=$true)][Guid]$ExpectedMvid,
		[Parameter(Mandatory=$true)][scriptblock]$InvokeRpc,
		[string]$NamePattern,
		[Nullable[int]]$ProcessId,
		[string]$RuntimeGuid,
		[string]$AppDomainId
	)
	$expected=$ExpectedMvid.ToString('D'); $offset=0; $candidates=@()
	do {
		$arguments=@{session_id=$SessionId;offset=$offset;count=500}
		if(-not [string]::IsNullOrWhiteSpace($NamePattern)){$arguments.name_pattern=$NamePattern}
		$page=& $InvokeRpc 'list_modules' $arguments; $rows=@($page.modules); $candidates+=$rows; $offset+=$rows.Count
	} while($page.truncated -and $rows.Count -gt 0)
	if($ProcessId.HasValue){$candidates=@($candidates|Where-Object{$_.process_id -eq $ProcessId.Value})}
	if(-not [string]::IsNullOrWhiteSpace($RuntimeGuid)){$candidates=@($candidates|Where-Object{$_.runtime_guid -eq $RuntimeGuid})}
	if(-not [string]::IsNullOrWhiteSpace($AppDomainId)){$candidates=@($candidates|Where-Object{[string]$_.app_domain_id -eq $AppDomainId})}
	$matches=@(); $diagnostics=@()
	foreach($candidate in $candidates){
		if([string]::IsNullOrWhiteSpace([string]$candidate.module_id)){continue}
		try {
			$metadata=& $InvokeRpc 'get_metadata' @{session_id=$SessionId;module_id=$candidate.module_id}
			$diagnostics+=Format-DgSpyModuleCandidate $candidate ([string]$metadata.mvid)
			$actual=[Guid]::Empty
			if([Guid]::TryParse([string]$metadata.mvid,[ref]$actual)-and $actual -eq $ExpectedMvid){$matches+=$candidate}
		} catch {$diagnostics+=Format-DgSpyModuleCandidate $candidate ('metadata unavailable: '+$_.Exception.Message)}
	}
	if($matches.Count -eq 1){return [string]$matches[0].module_id}
	$hint=if([string]::IsNullOrWhiteSpace($NamePattern)){'<none>'}else{$NamePattern}
	$detail=if($diagnostics.Count -eq 0){'  <no candidates>'}else{'  '+($diagnostics -join "`n  ")}
	if($matches.Count -eq 0){throw "No loaded module has MVID $expected (name_pattern=$hint). Candidates:`n$detail"}
	$matching=@($matches|ForEach-Object{Format-DgSpyModuleCandidate $_ $expected})
	throw "MVID $expected is ambiguous across $($matches.Count) loaded module instances. Pass an exact process/runtime/AppDomain hint; no module was selected:`n  $($matching -join "`n  ")"
}

function Format-DgSpyModuleCandidate {
	param($Candidate,[string]$MvidState)
	"module_id=$($Candidate.module_id) name=$($Candidate.name) filename=$($Candidate.filename) process_id=$($Candidate.process_id) runtime_guid=$($Candidate.runtime_guid) app_domain_id=$($Candidate.app_domain_id) app_domain_name=$($Candidate.app_domain_name) order=$($Candidate.order) mvid=$MvidState"
}
