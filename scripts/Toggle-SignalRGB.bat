@echo off
setlocal
set "PROCESS_NAME=SignalRgb.exe"
set "SIGNAL_RGB=C:\Users\flowi\AppData\Local\VortxEngine\app-2.5.80\Signal-x64\SignalRgb.exe"

tasklist /FI "IMAGENAME eq %PROCESS_NAME%" /NH | findstr /I /L /C:"%PROCESS_NAME%" >nul
if errorlevel 1 goto launch

taskkill /IM "%PROCESS_NAME%" /F
exit /b %errorlevel%

:launch
if not exist "%SIGNAL_RGB%" (
    echo SignalRGB executable not found: "%SIGNAL_RGB%" >&2
    exit /b 1
)
start "" "%SIGNAL_RGB%"
exit /b %errorlevel%
