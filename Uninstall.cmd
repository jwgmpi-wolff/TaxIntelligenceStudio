@echo off
setlocal
title Uninstall Tax Intelligence Studio

rem Double-click this file to remove Tax Intelligence Studio from this computer.
rem Your data under %LOCALAPPDATA%\TaxIntelligenceStudio is kept.

set "PS=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"
if defined PROCESSOR_ARCHITEW6432 if exist "%SystemRoot%\Sysnative\WindowsPowerShell\v1.0\powershell.exe" set "PS=%SystemRoot%\Sysnative\WindowsPowerShell\v1.0\powershell.exe"

"%PS%" -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Install.ps1" -Uninstall
set "RESULT=%ERRORLEVEL%"
echo.
if not "%RESULT%"=="0" echo Uninstall did not complete. Review the messages above.
pause
exit /b %RESULT%