@echo off
rem ============================================================
rem  Capture "nvidia-smi dmon -s t" pour GPU-Monitor v1.4
rem  Double-cliquer sur un PC equipe d'une carte NVIDIA.
rem  Dure environ 12 secondes. Le resultat s'ouvre dans le Bloc-notes.
rem ============================================================
setlocal

set "OUT=%USERPROFILE%\Desktop\capture-dmon.txt"
if exist "%OUT%" del "%OUT%"

rem --- Trouver nvidia-smi ---
set "SMI="
where nvidia-smi >nul 2>nul
if not errorlevel 1 set "SMI=nvidia-smi"
if not defined SMI if exist "%SystemRoot%\System32\nvidia-smi.exe" set "SMI=%SystemRoot%\System32\nvidia-smi.exe"
if not defined SMI if exist "%ProgramFiles%\NVIDIA Corporation\NVSMI\nvidia-smi.exe" set "SMI=%ProgramFiles%\NVIDIA Corporation\NVSMI\nvidia-smi.exe"

echo ============================================================ > "%OUT%"
echo CAPTURE NVIDIA-SMI  %DATE% %TIME% >> "%OUT%"
echo ============================================================ >> "%OUT%"

if not defined SMI (
    echo nvidia-smi introuvable : aucun pilote NVIDIA detecte sur ce PC. >> "%OUT%"
    echo Ce PC ne peut pas produire la capture. >> "%OUT%"
    goto :fin
)

echo. >> "%OUT%"
echo --- CHEMIN DE nvidia-smi --- >> "%OUT%"
echo %SMI% >> "%OUT%"

echo. >> "%OUT%"
echo --- CARTE / PILOTE / LIAISON PCIe --- >> "%OUT%"
"%SMI%" --query-gpu=index,name,driver_version,pci.bus_id,pcie.link.gen.current,pcie.link.gen.max,pcie.link.width.current,pcie.link.width.max --format=csv >> "%OUT%" 2>&1

echo. >> "%OUT%"
echo --- dmon -s t   (5 secondes, 1 ligne par seconde et par carte) --- >> "%OUT%"
"%SMI%" dmon -s t >> "%OUT%" 2>&1

echo. >> "%OUT%"
echo --- dmon -s t -c 4   (variante : lignes de donnees prefixees par #) --- >> "%OUT%"
"%SMI%" dmon -s t -c 4 >> "%OUT%" 2>&1

echo. >> "%OUT%"
echo --- dmon -s put -c 2   (variante : colonnes melangees) --- >> "%OUT%"
"%SMI%" dmon -s put -c 2 >> "%OUT%" 2>&1

:fin
echo.
echo Fichier cree sur le Bureau : capture-dmon.txt
echo Il s'ouvre dans le Bloc-notes.
echo Copiez-collez son contenu dans la conversation.
echo.
start notepad "%OUT%"
endlocal
