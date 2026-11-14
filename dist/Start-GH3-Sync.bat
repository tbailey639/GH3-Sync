@echo off
title GH3-Sync Server
cd /d "%~dp0"
echo Starting GH3-Sync...
start http://localhost:7373
GH3-Sync.exe
pause
