$watcherPath = Join-Path $PSScriptRoot 'HookLab.Watcher.exe'
$watcherProcess = Start-Process -FilePath $watcherPath -ArgumentList 'run-installed' -WindowStyle Hidden -Wait -PassThru
exit $watcherProcess.ExitCode
