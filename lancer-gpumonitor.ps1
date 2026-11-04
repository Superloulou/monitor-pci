# Lance le moniteur GPU / PCIe.
# L'app est compilee EN MEMOIRE (Add-Type) car les .exe crees sur cette machine
# sont bloques par la strategie de controle des applications.
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
                          System.Drawing.dll, System.Management.dll

if ($SelfTest) {
    [SelfTest]::Run()
} else {
    Write-Host 'Ouverture de la fenetre.' -ForegroundColor Green
    [Program]::Main()
}
