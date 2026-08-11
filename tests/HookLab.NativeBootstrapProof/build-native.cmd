@echo off
setlocal
call "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\Common7\Tools\VsDevCmd.bat" -arch=x64 -host_arch=x64 >nul
if errorlevel 1 exit /b %errorlevel%
if not exist "%~dp0artifacts" mkdir "%~dp0artifacts"
cl.exe /nologo /std:c++17 /EHsc /W4 /WX /O2 /LD "%~dp0HookLab.NativeBootstrapProof.Native\Bootstrap.cpp" /Fe:"%~dp0artifacts\HookLab.NativeBootstrapProof.Native.dll" /link mscoree.lib
exit /b %errorlevel%
