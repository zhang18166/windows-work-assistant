@echo off
setlocal
cd /d "%~dp0"
if not exist ".venv\Scripts\python.exe" (
    call setup_env.bat
    if errorlevel 1 exit /b 1
)
".venv\Scripts\python.exe" excel_auto_pdf_gui.py
if errorlevel 1 (
    echo Application exited with an error. Run setup_env.bat to repair dependencies.
    pause
    exit /b 1
)
exit /b 0
