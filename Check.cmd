@echo off
setlocal
cd /d "%~dp0"
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
set "DOTNET_GENERATE_ASPNET_CERTIFICATE=false"
set "NUGET_PACKAGES=%~dp0.tools\packages"
set "SDK=dotnet"
if exist ".tools\dotnet\dotnet.exe" set "SDK=%~dp0.tools\dotnet\dotnet.exe"
"%SDK%" run --project tests\SecurityChecks\SecurityChecks.csproj -c Release
if errorlevel 1 goto failed
"%SDK%" run --project tests\UiChecks\UiChecks.csproj -c Release
if errorlevel 1 goto failed
echo.
echo All checks passed using fake account data.
exit /b 0
:failed
echo.
echo A check failed. See the output above.
pause
exit /b 1
