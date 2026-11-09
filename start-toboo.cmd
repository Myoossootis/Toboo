@echo off
setlocal
cd /d "%~dp0"
where dotnet >nul 2>&1
if errorlevel 1 (
  echo .NET 8 SDK is required for source builds.
  echo Download the portable app: https://github.com/Myoossootis/Toboo/releases/latest
  pause
  exit /b 1
)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\start-toboo.ps1" %*
if errorlevel 1 (
  echo toboo could not start. See the error above.
  pause
  exit /b 1
)
endlocal
