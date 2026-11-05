# Lance le moniteur GPU / PCIe.
# Voie de secours : l'app est compilee EN MEMOIRE (Add-Type), aucun .exe n'est
# ecrit sur le disque. Utile si GpuMonitor.exe est bloque (AppLocker / WDAC /
# SmartScreen) ou si l'on ne veut pas compiler. En temps normal, preferez
# GpuMonitor.exe (ou GpuMonitor.cmd).
# Parametre optionnel : -SelfTest  (verifie les moteurs sans ouvrir la fenetre)

param([switch]$SelfTest)

$ErrorActionPreference = 'Stop'
$src = Join-Path $PSScriptRoot 'GpuMonitor.cs'

if (-not (Test-Path -LiteralPath $src)) {
    Write-Error "Fichier source introuvable : $src"
    exit 1
}

Write-Host 'Compilation en memoire (quelques secondes)...' -ForegroundColor Cyan

Add-Type -TypeDefinition (Get-Content -LiteralPath $src -Raw) `
    -ReferencedAssemblies System.dll, System.Core.dll, System.Windows.Forms.dll, `
                          System.Drawing.dll, System.Management.dll, `
                          System.Windows.Forms.DataVisualization.dll

if ($SelfTest) {
    [SelfTest]::Run()
} else {
    Write-Host 'Ouverture de la fenetre.' -ForegroundColor Green
    # Main attend un tableau de parametres : on lui passe un tableau vide.
    [Program]::Main([string[]]@())
}
