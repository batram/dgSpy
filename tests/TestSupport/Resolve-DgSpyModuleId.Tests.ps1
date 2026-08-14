#requires -Version 5.1
$ErrorActionPreference='Stop'
$OutputEncoding=[Console]::OutputEncoding=[Text.UTF8Encoding]::new()
. (Join-Path $PSScriptRoot 'Resolve-DgSpyModuleId.ps1')
function Assert-Equal($Expected,$Actual,[string]$Label){if($Expected -ne $Actual){throw "$Label expected '$Expected', got '$Actual'"}}
function Assert-Throws([scriptblock]$Action,[string[]]$ExpectedText){try{& $Action;throw 'request unexpectedly succeeded'}catch{foreach($text in $ExpectedText){if($_.Exception.Message -notlike ('*'+$text+'*')){throw "error did not contain '$text': $($_.Exception.Message)"}}}}
function New-Row([string]$Id,[string]$Name,[int]$Domain,[int]$Order){[pscustomobject]@{module_id=$Id;name=$Name;filename="C:\fixtures\$Name";process_id=42;runtime_guid='runtime-1';app_domain_id=$Domain;app_domain_name="domain-$Domain";order=$Order}}
$wanted=[Guid]::NewGuid();$other=[Guid]::NewGuid()
$rows=@((New-Row 'dm1:wrong' 'Duplicate.dll' 1 1),(New-Row 'dm1:right' 'Duplicate.dll' 1 2));$mvids=@{'dm1:wrong'=$other.ToString('D');'dm1:right'=$wanted.ToString('D')}
$rpc={param($operation,$arguments)if($operation -eq 'list_modules'){[pscustomobject]@{modules=$rows;truncated=$false}}else{[pscustomobject]@{mvid=$mvids[[string]$arguments.module_id]}}}.GetNewClosure()
Assert-Equal 'dm1:right' (Resolve-DgSpyModuleId -SessionId s1 -ExpectedMvid $wanted -NamePattern Duplicate -InvokeRpc $rpc) 'duplicate-name selection'
$rows=@((New-Row 'dm1:domain-1' 'Same.dll' 1 1),(New-Row 'dm1:domain-2' 'Same.dll' 2 2));$mvids=@{'dm1:domain-1'=$wanted.ToString('D');'dm1:domain-2'=$wanted.ToString('D')}
$rpc={param($operation,$arguments)if($operation -eq 'list_modules'){[pscustomobject]@{modules=$rows;truncated=$false}}else{[pscustomobject]@{mvid=$mvids[[string]$arguments.module_id]}}}.GetNewClosure()
Assert-Throws {Resolve-DgSpyModuleId -SessionId s1 -ExpectedMvid $wanted -NamePattern Same -InvokeRpc $rpc} @('ambiguous across 2','dm1:domain-1','app_domain_id=1','dm1:domain-2','app_domain_id=2','no module was selected')
Assert-Equal 'dm1:domain-2' (Resolve-DgSpyModuleId -SessionId s1 -ExpectedMvid $wanted -NamePattern Same -AppDomainId 2 -InvokeRpc $rpc) 'AppDomain-qualified selection'
Assert-Throws {Resolve-DgSpyModuleId -SessionId s1 -ExpectedMvid ([Guid]::NewGuid()) -NamePattern Same -InvokeRpc $rpc} @('No loaded module has MVID','dm1:domain-1','mvid=')
Write-Host 'PASSED  exact MVID module resolver duplicate-name and AppDomain ambiguity contracts'
