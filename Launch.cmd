@echo off
cd /d "%~dp0"
echo Open http://127.0.0.1:5080 in your browser after the server starts.
dotnet run --project GC.Web.csproj
if errorlevel 1 pause
