@echo off
setlocal
echo ==========================================================
echo  Building Portable Single-File Executable: WinToRTSP
echo ==========================================================

cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File ".\publish-portable.ps1"

if %ERRORLEVEL% NEQ 0 (
    echo [ERROR] Build failed with exit code %ERRORLEVEL%
    pause
    exit /b %ERRORLEVEL%
)

echo [SUCCESS] Portable build completed.
pause
