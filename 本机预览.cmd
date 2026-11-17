@echo off
setlocal
cd /d "%~dp0" || exit /b 1
if not exist "%~dp0TelemetryMonitor.exe" (
  echo ERROR: TelemetryMonitor.exe was not found next to this launcher.
  echo Extract the complete offline package before starting the application.
  pause
  exit /b 2
)
echo Starting local preview. Leave this window open.
echo The server will print the browser address below.
"%~dp0TelemetryMonitor.exe" --local %*
set "LauncherExitCode=%ERRORLEVEL%"
if not "%LauncherExitCode%"=="0" (
  echo Server exited with error code %LauncherExitCode%.
  pause
)
exit /b %LauncherExitCode%
