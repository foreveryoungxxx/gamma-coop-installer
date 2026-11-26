@echo off
cd /d "%~dp0"
net session >nul 2>&1
if errorlevel 1 goto :needadmin
where wt >nul 2>&1
if errorlevel 1 goto :direct
if not "%WT_SESSION%"=="" goto :direct
start "" wt.exe -w new --title "GAMMA Coop 0.95" powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0installer\gamma_coop.ps1"
exit /b
:direct
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0installer\gamma_coop.ps1"
exit /b
:needadmin
powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
exit /b
