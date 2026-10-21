@echo off
chcp 65001 >nul
setlocal
cd /d "%~dp0"
if not exist ".venv\Scripts\python.exe" (
    call setup_env.bat
    if errorlevel 1 exit /b 1
)
".venv\Scripts\python.exe" tools\build_release.py
if errorlevel 1 (
    echo Build failed. Check the error above and run setup_env.bat if needed.
    pause
    exit /b 1
)
echo Build complete. Output: dist\vVERSION\Excel自动转PDFvVERSION.exe
pause
exit /b 0
