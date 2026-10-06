@echo off
rem In-place releases, with path checks and scoped process shutdown in PowerShell.
setlocal
set "MODE=%~1"
if "%MODE%"=="" set "MODE=both"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0publish.ps1" -Mode "%MODE%"
exit /b %errorlevel%
