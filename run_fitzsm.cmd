@echo off
REM Fitz Labs single-machine runner (NT8 SIM). Usage: run_fitzsm.cmd [live]
REM Without "live" it runs DRY_RUN (no orders). Logs: runs\current\decisions.jsonl
setlocal
cd /d "%~dp0"
if not exist ".venv\Scripts\python.exe" (echo Missing .venv - see README.md step 3 & exit /b 1)
if not exist "config\fitzsm.json" (echo Missing config\fitzsm.json - copy config\fitzsm.example.json & exit /b 1)
set OUT=runs\current
if /I "%1"=="live" (
  ".venv\Scripts\python.exe" -m fitzsm --config config\fitzsm.json run --live --out %OUT%
) else (
  ".venv\Scripts\python.exe" -m fitzsm --config config\fitzsm.json run --out %OUT%
)
exit /b %ERRORLEVEL%
