@echo off
rem Builds release packages for FlashTrans into dist\.
rem   tools\publish.cmd          both flavours
rem   tools\publish.cmd fast     self-contained + ReadyToRun (recommended)
rem   tools\publish.cmd small    framework-dependent, needs .NET 9 Desktop Runtime
setlocal
set MODE=%1
if "%MODE%"=="" set MODE=both
set ROOT=%~dp0..
set PROJ=%ROOT%\src\FlashTrans\FlashTrans.csproj
set DOTNET_CLI_UI_LANGUAGE=en

if "%MODE%"=="fast" goto :check
if "%MODE%"=="small" goto :check
if "%MODE%"=="both" goto :check
echo unknown mode "%MODE%" - use fast, small or both
exit /b 1

:check
rem A running instance locks dist\...\FlashTrans.exe and publish then dies with an
rem unreadable MSB4018 "GenerateBundle task failed". Say what's actually wrong.
rem No pipe to find/findstr here: under Git Bash those resolve to the Unix tools.
set RUNNING=
for /f "tokens=1" %%p in ('tasklist /fi "imagename eq FlashTrans.exe" /nh 2^>nul') do (
    if /i "%%p"=="FlashTrans.exe" set RUNNING=1
)
if defined RUNNING (
    echo ERROR: FlashTrans.exe is running and locks the files in dist\.
    echo        Exit it first: tray icon - right-click - the last menu item.
    exit /b 1
)
if "%MODE%"=="small" goto :small

:fast
rem Fastest cold start: startup paths precompiled, no shared-framework probing.
set OUT=%ROOT%\dist\FlashTrans-win-x64-fast
if exist "%OUT%" rd /s /q "%OUT%"
echo === publishing FlashTrans-win-x64-fast ===
rem Single-file, but no compression: compressing gets to 62MB while pushing
rem cold start to 1.8-2.8s and working set to 175MB. Not worth it.
dotnet publish "%PROJ%" -c Release -r win-x64 -o "%OUT%" --nologo -v q ^
    --self-contained true -p:PublishReadyToRun=true ^
    -p:PublishReadyToRunComposite=false -p:PublishSingleFile=true ^
    -p:DebugType=none
if errorlevel 1 exit /b 1
echo   %OUT%\FlashTrans.exe
if not "%MODE%"=="both" goto :done

:small
rem Tiny on disk, but the machine needs the .NET 9 Desktop Runtime installed.
set OUT=%ROOT%\dist\FlashTrans-win-x64-small
if exist "%OUT%" rd /s /q "%OUT%"
echo === publishing FlashTrans-win-x64-small ===
dotnet publish "%PROJ%" -c Release -r win-x64 -o "%OUT%" --nologo -v q ^
    --self-contained false -p:DebugType=none
if errorlevel 1 exit /b 1
echo   %OUT%\FlashTrans.exe

:done
echo.
echo First run tips:
echo   FlashTrans.exe            show the main window
echo   FlashTrans.exe --tray     start hidden in the tray
echo   put a portable.txt next to the exe to keep settings in .\data
endlocal
