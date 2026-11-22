@echo off
set "HERE=%~dp0"
cd /d "%TEMP%"
powershell -NoProfile -ExecutionPolicy Bypass -File "%HERE%Uninstall.ps1" %* & pause & exit /b
