@echo off
setlocal DisableDelayedExpansion
rem Install the Good Night! effect from SignalRGB's Library first.
rem Double percent signs preserve URL escapes in a batch file.
start "" "signalrgb://effect/apply/Good%%20Night%%21"
if errorlevel 1 exit /b %errorlevel%
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Close-SignalRGB-To-Tray.ps1"
exit /b %errorlevel%
