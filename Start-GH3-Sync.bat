@echo off
title GH3-Sync Server
cd /d "%~dp0"
echo Starting GH3-Sync Beta 3...
if exist "dist\GH3-Sync.exe" (
    start http://localhost:7373
    "dist\GH3-Sync.exe"
) else if exist "bin\Release\net9.0-windows\GH3-Sync.exe" (
    start http://localhost:7373
    "bin\Release\net9.0-windows\GH3-Sync.exe"
) else (
    start http://localhost:7373
    dotnet run -c Release
)
pause
