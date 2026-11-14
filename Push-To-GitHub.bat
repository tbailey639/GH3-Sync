@echo off
title Push GH3-Sync to GitHub
cd /d "G:\Guitar Hero Songs\GH3-Sync"
echo ===================================================
echo   Pushing GH3-Sync to https://github.com/tbailey639/GH3-Sync
echo ===================================================
echo.
git add .
git commit -m "Update GH3-Sync" 2>nul
git pull origin main --rebase
git push origin main
echo.
echo ===================================================
echo   Finished! All changes are live on GitHub.
echo ===================================================
pause
