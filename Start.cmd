@echo off
setlocal
cd /d "%~dp0"
if not exist "dist\v1.9.0\RobloxAccountManager.exe" (
  echo The app has not been built yet. Run Build.cmd first.
  pause
  exit /b 1
)
start "" "dist\v1.9.0\RobloxAccountManager.exe"
