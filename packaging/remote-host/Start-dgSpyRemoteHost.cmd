@echo off
setlocal
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Start-dgSpyRemoteHost.ps1" %*
exit /b %ERRORLEVEL%
