param(
	[string]$OutputDirectory,
	[ValidateSet('All','Same','ElevatedHost','ElevatedTarget')][string]$Case = 'All'
)

$ErrorActionPreference = 'Stop'
$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new()
$repoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$harness = Join-Path $PSScriptRoot 'Harness\bin\Release\net48\HookLab.Transport.Harness.exe'
if (-not (Test-Path -LiteralPath $harness)) { throw "Harness not built: $harness" }
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { $OutputDirectory = Join-Path $env:TEMP ('hooklab-integrity-' + [Guid]::NewGuid().ToString('N')) }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

function Wait-File {
	param([string]$Path)
	$deadline = [DateTime]::UtcNow.AddSeconds(10)
	while (-not (Test-Path -LiteralPath $Path) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 50 }
	if (-not (Test-Path -LiteralPath $Path)) { throw "Timed out waiting for $Path" }
}

function Run-Client {
	param([string]$Root, [string]$Result, [switch]$Elevated)
	$arguments = @('-Mode','client','-StateRoot',$Root,'-Seconds','1','-Result',$Result)
	if ($Elevated) { $process = Start-Process -FilePath $harness -ArgumentList $arguments -Verb RunAs -Wait -PassThru }
	else { $process = Start-Process -FilePath $harness -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru }
	if ($process.ExitCode -ne 0) { throw "Client exited with $($process.ExitCode)" }
}

function Run-Case {
	param([string]$Name, [switch]$ElevatedTarget, [switch]$ElevatedClient)
	$root = Join-Path $OutputDirectory $Name
	$ready = Join-Path $root 'target.txt'; $result = Join-Path $root 'client.txt'
	New-Item -ItemType Directory -Path $root -Force | Out-Null
	$arguments = @('-Mode','serve','-StateRoot',$root,'-Seconds','10','-Result',$ready)
	if ($ElevatedTarget) { $target = Start-Process -FilePath $harness -ArgumentList $arguments -Verb RunAs -PassThru }
	else { $target = Start-Process -FilePath $harness -ArgumentList $arguments -WindowStyle Hidden -PassThru }
	try { Wait-File $ready; Run-Client -Root $root -Result $result -Elevated:$ElevatedClient; Wait-File $result }
	finally {
		if (-not $target.HasExited -and $ElevatedTarget) { $target.WaitForExit(15000) | Out-Null }
		elseif (-not $target.HasExited) { Stop-Process -Id $target.Id -Force }
	}
	[pscustomobject]@{ Case=$Name; Target=[string](Get-Content -Raw $ready); Client=[string](Get-Content -Raw $result) }
}

$results = @()
if ($Case -in @('All','Same')) { $results += Run-Case -Name 'same-integrity' }
if ($Case -in @('All','ElevatedHost')) { $results += Run-Case -Name 'elevated-host-medium-target' -ElevatedClient }
if ($Case -in @('All','ElevatedTarget')) { $results += Run-Case -Name 'medium-host-elevated-target' -ElevatedTarget }
$results | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'integrity-results.json') -Encoding UTF8
$results
