@echo off
rem ============================================================
rem  Moniteur GPU / PCIe - lanceur (double-clic)
rem  Si GpuMonitor.exe est absent, il est compile automatiquement.
rem ============================================================
setlocal
cd /d "%~dp0"

if exist "GpuMonitor.exe" goto lancer

echo Compilation en cours (environ 5 secondes)...
call installer.cmd
if not exist "GpuMonitor.exe" (
    echo.
    echo ECHEC : la compilation a echoue. Voir le message ci-dessus.
    pause
    exit /b 1
)

:lancer
start "" "GpuMonitor.exe"
endlocal
