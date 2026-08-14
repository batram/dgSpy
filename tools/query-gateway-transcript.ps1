#requires -Version 5.1
[CmdletBinding()]
param(
	[Parameter(Mandatory=$true)][string]$Path,
	[string]$Operation,[string]$ErrorCode,[string]$SessionId,[string]$HostId,[string]$ClientId,[string]$CorrelationId,[string]$Contains,
	[ValidateRange(1,10000)][int]$Tail=50,[switch]$Raw
)
$ErrorActionPreference='Stop'
$OutputEncoding=[Console]::OutputEncoding=[Text.UTF8Encoding]::new()
$files=@();if(Test-Path -LiteralPath ($Path+'.1')){$files+=($Path+'.1')};if(Test-Path -LiteralPath $Path){$files+=$Path};if($files.Count -eq 0){throw "Transcript was not found: $Path"}
$records=foreach($file in $files){foreach($line in Get-Content -LiteralPath $file){if([string]::IsNullOrWhiteSpace($line)){continue};try{$line|ConvertFrom-Json}catch{Write-Warning "Skipping malformed JSONL in ${file}: $($_.Exception.Message)"}}}
$records=@($records|Where-Object{
	([string]::IsNullOrWhiteSpace($Operation)-or$_.operation -eq $Operation)-and([string]::IsNullOrWhiteSpace($ErrorCode)-or$_.error_code -eq $ErrorCode)-and
	([string]::IsNullOrWhiteSpace($SessionId)-or$_.session_id -eq $SessionId)-and([string]::IsNullOrWhiteSpace($HostId)-or$_.host_id -eq $HostId)-and
	([string]::IsNullOrWhiteSpace($ClientId)-or$_.client_id -eq $ClientId)-and([string]::IsNullOrWhiteSpace($CorrelationId)-or$_.correlation_id -eq $CorrelationId)-and
	([string]::IsNullOrWhiteSpace($Contains)-or($_|ConvertTo-Json -Compress -Depth 30).IndexOf($Contains,[StringComparison]::OrdinalIgnoreCase)-ge 0)
}|Select-Object -Last $Tail)
if($Raw){$records|ForEach-Object{$_|ConvertTo-Json -Compress -Depth 30}}else{$records|Select-Object timestamp_utc,operation,outcome,error_code,duration_ms,client_id,host_id,session_id,correlation_id,request_truncated,response_truncated|Format-Table -AutoSize}
