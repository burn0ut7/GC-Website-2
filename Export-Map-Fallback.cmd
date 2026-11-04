@echo off
cd /d "%~dp0"
python -X utf8 scripts\export-map-fallback.py %*
pause
