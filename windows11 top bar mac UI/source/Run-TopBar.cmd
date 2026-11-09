@echo off
cd /d "%~dp0"
dotnet run --project "%~dp0src\TopBar\TopBar.csproj"
if errorlevel 1 pause
