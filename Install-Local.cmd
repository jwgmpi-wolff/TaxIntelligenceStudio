@echo off
setlocal EnableExtensions
cd /d "%~dp0"
title Tax Intelligence Studio - Local Installation

if not exist "%~dp0scripts\Install.ps1" (
    echo.
    echo ERROR: scripts\Install.ps1 was not found.
    echo Extract the complete repository ZIP before running this file.
    echo.
    pause
    exit /b 1
)

echo.
echo Installing Tax Intelligence Studio on this computer...
echo This may take several minutes.
echo.

"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" ^
    -NoLogo ^
    -NoProfile ^
    -ExecutionPolicy Bypass ^
    -File "%~dp0scripts\Install.ps1" ^
    -DesktopShortcut ^
    -Launch

set "INSTALL_EXIT_CODE=%ERRORLEVEL%"
echo.

if not "%INSTALL_EXIT_CODE%"=="0" (
    echo INSTALLATION FAILED with exit code %INSTALL_EXIT_CODE%.
    echo Review the error above and the logs in C:\temp\TaxIntelligenceStudio.
    echo.
    pause
    exit /b %INSTALL_EXIT_CODE%
)

echo Installation completed successfully.
echo You can launch Tax Intelligence Studio from the Desktop or Start Menu.
echo.
pause
exit /b 0
