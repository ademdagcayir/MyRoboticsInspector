@echo off
REM Launch local Mosquitto broker with dev config (anonymous, all interfaces, 1883).
REM Stop with Ctrl+C.
setlocal
set "MOSQ=C:\Program Files\mosquitto\mosquitto.exe"
if not exist "%MOSQ%" (
    echo Mosquitto not found at %MOSQ%
    echo Install with: winget install -e --id EclipseFoundation.Mosquitto
    exit /b 1
)
cd /d "%~dp0"
echo Starting Mosquitto on 0.0.0.0:1883 ^(anonymous, dev config^)
"%MOSQ%" -c mosquitto-dev.conf -v
