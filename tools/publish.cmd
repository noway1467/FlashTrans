@echo off
rem Versioned releases, with filesystem checks implemented entirely in PowerShell.
setlocal
set "MODE=%~1"
if "%MODE%"=="" set "MODE=both"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0publish.ps1" -Mode "%MODE%"
exit /b %errorlevel%
