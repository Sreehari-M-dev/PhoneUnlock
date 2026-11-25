@echo off
setlocal
cd /d "%~dp0"

rem Re-launch this same file elevated if we are not already administrator.
net session >nul 2>&1
if not "%errorlevel%"=="0" (
    echo Requesting administrator privileges...
    powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

echo ========================================
echo   Phone Unlock - Installer
echo ========================================
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\install.ps1"
set "RC=%errorlevel%"

echo.
if not "%RC%"=="0" (
    echo Installer exited with code %RC%.
    echo Check the messages above.
) else (
    echo Installer finished.
)
echo.
pause
exit /b %RC%
