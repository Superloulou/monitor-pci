<#
    lancer-sans-exe.ps1 - Voie de secours : le code est compile EN MEMOIRE
    (Add-Type), aucun .exe n'est ecrit sur le disque. Utile si GpuMonitor.exe
    est bloque (AppLocker / WDAC / SmartScreen) ou si l'on ne veut pas compiler.
    En temps normal, preferez GpuMonitor.exe (ou GpuMonitor.cmd).

    Le programme est compile EN MEMOIRE par PowerShell : aucun fichier
    .exe n'est cree sur le disque.

    Usage :
        powershell -NoProfile -ExecutionPolicy Bypass -STA -File .\lancer-sans-exe.ps1
        powershell -NoProfile -ExecutionPolicy Bypass -STA -File .\lancer-sans-exe.ps1 -SelfTest
#>
param([switch]$SelfTest)

$ErrorActionPreference = 'Stop'
$src = Join-Path $PSScriptRoot 'GpuMonitor.cs'

if (-not (Test-Path -LiteralPath $src)) {
    Write-Error "Fichier source introuvable : $src"
    exit 1
}

Write-Host 'Compilation en memoire (3 a 6 secondes)...'
Add-Type -TypeDefinition (Get-Content -LiteralPath $src -Raw) `
    -ReferencedAssemblies System.dll,System.Core.dll,System.Windows.Forms.dll,System.Drawing.dll,System.Management.dll,System.Windows.Forms.DataVisualization.dll

if ($SelfTest) {
    [SelfTest]::Run()
} else {
    # Main attend un string[] : on lui passe un tableau de chaines vide.
    [Program]::Main([string[]]@())
}
