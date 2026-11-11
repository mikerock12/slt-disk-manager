@echo off
rem Reinicia este computador em modo SLT Disk Manager (cliente ja conectado ao servidor).
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Iniciar-Cliente.ps1" -SltMode
