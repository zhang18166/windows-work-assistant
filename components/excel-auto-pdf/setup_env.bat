@echo off
setlocal
cd /d "%~dp0"
if exist ".venv\Scripts\python.exe" goto install
py -3.12 -c "import sys; assert sys.version_info[:2] == (3, 12)" >nul 2>&1
if not errorlevel 1 (
    py -3.12 -m venv .venv
    if errorlevel 1 goto failed
    goto install
)
python -c "import sys; assert sys.version_info[:2] == (3, 12)" >nul 2>&1
if errorlevel 1 (
    echo Please install Python 3.12 for Windows with tkinter and pip enabled.
    echo Then run setup_env.bat again.
    pause
    exit /b 1
)
python -m venv .venv
if errorlevel 1 goto failed
:install
".venv\Scripts\python.exe" -m pip install -r requirements.txt
if errorlevel 1 goto failed
".venv\Scripts\python.exe" -c "import tkinter, pythoncom, win32com.client, watchdog, pystray, PIL, PyInstaller, pypdf"
if errorlevel 1 goto failed
echo Environment is ready. Run run_gui.bat to start the application.
exit /b 0
:failed
echo Setup failed. Check Python, network access and the error above.
pause
exit /b 1
