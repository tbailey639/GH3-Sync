@echo off
title GH3-Sync Beta 5
cd /d "%~dp0"
if exist "GH3-Sync.exe" (
    start "" "GH3-Sync.exe"
) else if exist "dist\GH3-Sync.exe" (
    start "" "dist\GH3-Sync.exe"
) else (
    echo GH3-Sync.exe not found! Run dotnet publish first.
    pause
)
