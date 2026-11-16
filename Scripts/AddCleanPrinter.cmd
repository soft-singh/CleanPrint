@echo off
rem Double-click to run. Asks for Administrator rights (UAC) and keeps the window open.
cd /d "%~dp0"
net session >nul 2>&1
if not "%errorlevel%"=="0" (
    echo Requesting Administrator rights...
    powershell.exe -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0AddCleanPrinter.ps1" %*
set RC=%errorlevel%
echo.
pause
exit /b %RC%
