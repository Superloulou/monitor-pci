<#
    lancer-sans-exe.ps1 - Alternative quand le .exe est bloque
    (strategie de securite type AppLocker / WDAC / SmartScreen).

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
    -ReferencedAssemblies System.dll,System.Core.dll,System.Windows.Forms.dll,System.Drawing.dll,System.Management.dll

if ($SelfTest) { [SelfTest]::Run() } else { [Program]::Main() }
