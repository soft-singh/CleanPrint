@echo off
rem Wrapper for Publish.ps1 (builds into ..\Publish).
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Publish.ps1" %*
set RC=%errorlevel%
if not "%RC%"=="0" echo Publish FAILED (exit code %RC%).
pause
exit /b %RC%
