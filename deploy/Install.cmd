@echo off
rem Double-click installer: elevates and runs install.ps1 without needing to change execution policy.
net session >nul 2>&1
if %errorlevel% neq 0 (
    powershell -NoProfile -Command "Start-Process -Verb RunAs -FilePath '%~f0'"
    exit /b
)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" %*
pause
