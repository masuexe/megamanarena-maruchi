@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Start-DedicatedServer.ps1" -StopServer
pause
