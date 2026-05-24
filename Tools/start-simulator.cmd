@echo off
REM Launch the simulator. Defaults: localhost:1883, myrobotics/robot1
cd /d "%~dp0\RobotSimulator"
dotnet run --no-build -c Release -- %*
if errorlevel 1 (
    echo Build needed, running with restore...
    dotnet run -c Release -- %*
)
