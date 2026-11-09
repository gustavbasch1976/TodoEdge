@echo off
cd /d "%~dp0"

where dotnet >nul 2>nul
if errorlevel 1 (
  echo The .NET 8 SDK is not installed.
  echo Install it by running this in a terminal, then run build.bat again:
  echo     winget install Microsoft.DotNet.SDK.8
  pause
  exit /b 1
)

echo Building TodoEdge...
dotnet publish TodoEdge.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
if errorlevel 1 (
  echo.
  echo Build failed. Copy the error lines above and send them to Claude.
  pause
  exit /b 1
)

echo.
echo Done. Your app is here: %~dp0publish\TodoEdge.exe
start "" "%~dp0publish\TodoEdge.exe"
pause
