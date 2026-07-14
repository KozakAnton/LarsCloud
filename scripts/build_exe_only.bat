@echo off
setlocal
cd /d "%~dp0.."
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-installer.ps1" -ExeOnly %*
if errorlevel 1 (
  echo.
  echo Build failed. Read the error above.
  pause
  exit /b 1
)
echo.
echo LarsCloud.exe is ready in artifacts\publish
pause
