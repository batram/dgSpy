$ErrorActionPreference = 'Stop'
$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new()

$path = Join-Path $PSScriptRoot 'Start-DgSpyHost.ps1'
$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($path, [ref]$tokens, [ref]$errors)
if ($errors.Count -ne 0) {
	throw "Start-DgSpyHost.ps1 has PowerShell parse errors: $($errors.Message -join '; ')"
}

$protectedCheck = $ast.FindAll({
	param($node)
	if ($node -isnot [Management.Automation.Language.TryStatementAst]) { return $false }
	$tryText = $node.Body.Extent.Text
	$catchText = @($node.CatchClauses | ForEach-Object { $_.Body.Extent.Text }) -join "`n"
	return $tryText -match 'Invoke-DgSpyRpc\s+-OperationName\s+''get_host_info''' -and
		$tryText -match 'GetFullPath\(\$identity\.extension_path\)' -and
		$catchText -match 'Stop-Process\s+-Id\s+\$process\.Id' -and
		$catchText -match '(?m)^\s*throw\s*$'
}, $true)

if (@($protectedCheck).Count -ne 1) {
	throw 'The host identity RPC and path validation must share a cleanup catch that stops the launched process and rethrows.'
}

Write-Host 'PASSED  Start-DgSpyHost identity-failure cleanup contract'
