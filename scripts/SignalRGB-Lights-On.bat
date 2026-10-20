@echo off
setlocal DisableDelayedExpansion
rem Apply the installed Rainbow effect.
start "" "signalrgb://effect/apply/Rainbow"
if errorlevel 1 exit /b %errorlevel%
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Close-SignalRGB-To-Tray.ps1"
exit /b %errorlevel%
