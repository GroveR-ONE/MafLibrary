@echo off
setlocal
cd /d "%~dp0"

echo Stopping the MAF library server...
taskkill /IM MafLibrary.exe /F >nul 2>&1

if errorlevel 1 (
    echo Server MafLibrary.exe is not running.
) else (
    echo Server stopped.
)

echo.
pause
