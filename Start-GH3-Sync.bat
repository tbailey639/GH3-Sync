@echo off
title GH3-Sync Server
cd /d "%~dp0"
echo Starting GH3-Sync...
if exist "bin\Debug\net10.0\GH3-Sync.exe" (
    start http://localhost:7373
    "bin\Debug\net10.0\GH3-Sync.exe"
) else (
    start http://localhost:7373
    dotnet run
)
pause
