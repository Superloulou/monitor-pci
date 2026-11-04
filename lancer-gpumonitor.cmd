@echo off
rem Double-cliquer pour lancer le moniteur GPU / PCIe.
start "" powershell.exe -NoProfile -ExecutionPolicy Bypass -STA -File "%~dp0lancer-gpumonitor.ps1"
