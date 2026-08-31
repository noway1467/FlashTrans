@echo off
rem Moves an already-published dist\_staging-fast into dist\FlashTrans-win-x64-fast.
rem
rem Why this exists: publish.cmd wipes and rewrites dist\FlashTrans-win-x64-fast, but a
rem running FlashTrans.exe holds its own files open, so publishing dies there. Publishing
rem to _staging-fast sidesteps the lock; this script parks the finished build in place once
rem the app is closed. Exit it from the tray icon - right-click - the last menu item.
setlocal
set ROOT=%~dp0..
set STAGE=%ROOT%\dist\_staging-fast
set OUT=%ROOT%\dist\FlashTrans-win-x64-fast

if not exist "%STAGE%\FlashTrans.exe" (
    echo ERROR: no staged build at %STAGE%
    echo        Run this first, with the app closed:  tools\publish.cmd fast
    exit /b 1
)

rem Wait for the instance to go away instead of killing it - it may be mid-edit and
rem taskkill would drop whatever is on screen.
rem No pipe to find/findstr here: under Git Bash those resolve to the Unix tools.
set /a WAITED=0
:wait
set RUNNING=
for /f "tokens=1" %%p in ('tasklist /fi "imagename eq FlashTrans.exe" /nh 2^>nul') do (
    if /i "%%p"=="FlashTrans.exe" set RUNNING=1
)
if not defined RUNNING goto :swap
if %WAITED%==0 echo Waiting for FlashTrans.exe to exit - close it from the tray icon...
if %WAITED% GEQ 600 (
    echo ERROR: still running after 10 minutes, giving up. Nothing was changed.
    exit /b 1
)
timeout /t 2 /nobreak >nul
set /a WAITED+=2
goto :wait

:swap
rem Keep the old build until the new one is in: if the move fails halfway there is
rem still something runnable on disk.
if exist "%OUT%.bak" rd /s /q "%OUT%.bak"
if exist "%OUT%" ren "%OUT%" "FlashTrans-win-x64-fast.bak"
if errorlevel 1 (
    echo ERROR: cannot rename %OUT% - something still has it open.
    exit /b 1
)
move "%STAGE%" "%OUT%" >nul
if errorlevel 1 (
    echo ERROR: move failed, putting the old build back.
    ren "%OUT%.bak" "FlashTrans-win-x64-fast"
    exit /b 1
)
rd /s /q "%OUT%.bak"

for /f "tokens=* usebackq" %%v in (`powershell -NoProfile -Command "(Get-Item '%OUT%\FlashTrans.exe').VersionInfo.FileVersion"`) do set VER=%%v
echo Swapped in %VER%
echo   %OUT%\FlashTrans.exe
endlocal
