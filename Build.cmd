@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Build-Release.ps1"
if errorlevel 1 (
  echo Build failed. See the error above. Close the app before rebuilding its files.
  pause
  exit /b 1
)
