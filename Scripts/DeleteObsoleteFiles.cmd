@echo off
rem Wrapper for DeleteObsoleteFiles.ps1. Pass -WhatIf to preview.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0DeleteObsoleteFiles.ps1" %*
set RC=%errorlevel%
pause
exit /b %RC%
