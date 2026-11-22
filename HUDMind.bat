@echo off
cd /d "%~dp0"
if exist "dist\native\HUDMind.Native.exe" (
    start "" "dist\native\HUDMind.Native.exe"
) else (
    dotnet run --project native\HUDMind.App\HUDMind.App.csproj -c Release
)
