@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Start-Arena8.ps1" -Role Client
if errorlevel 1 pause
