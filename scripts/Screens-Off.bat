@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Screens-Off.ps1"
exit /b %errorlevel%
