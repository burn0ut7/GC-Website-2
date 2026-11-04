@echo off
setlocal
cd /d "%~dp0"
python -u scripts\export-map.py %*
set "result=%errorlevel%"
if "%~1"=="" pause
exit /b %result%
