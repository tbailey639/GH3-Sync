@echo off
title Push GH3-Sync to GitHub
cd /d "G:\Guitar Hero Songs\GH3-Sync"
echo ===================================================
echo   Pushing GH3-Sync to https://github.com/tbailey639/GH3-Sync
echo ===================================================
echo.
git branch -M main
git push -u origin main
echo.
echo ===================================================
echo   Finished! Press any key to close this window.
echo ===================================================
pause
