@echo off
cd /d "%~dp0"
py -3 local-server.py
if errorlevel 1 pause
