@echo off
setlocal
title Install Tax Intelligence Studio

rem Double-click this file to build and install Tax Intelligence Studio on this
rem computer. Nothing needs to be edited or installed first.

rem Use the native 64-bit Windows PowerShell even if this window was started by
rem a 32-bit or emulated process, so the correct architecture is detected.
set "PS=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"
if defined PROCESSOR_ARCHITEW6432 if exist "%SystemRoot%\Sysnative\WindowsPowerShell\v1.0\powershell.exe" set "PS=%SystemRoot%\Sysnative\WindowsPowerShell\v1.0\powershell.exe"

"%PS%" -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Install.ps1" -DesktopShortcut -Launch
if errorlevel 1 (
    echo.
    echo Installation did not complete. Review the messages above.
    pause
    exit /b 1
)

echo.
echo Installation complete. This window will close in 15 seconds.
timeout /t 15 >nul
exit /b 0