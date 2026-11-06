@echo off
setlocal
cd /d "%~dp0"

set "CSC64=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
set "CSC32=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"

if exist "%CSC64%" goto compiler
if exist "%CSC32%" goto compiler

echo.
echo ERREUR : le compilateur .NET Framework 4.x est introuvable.
echo        Windows 11 l'installe par defaut. Verifiez la presence de :
echo        %CSC64%
echo.
echo Solution de contournement : lancez lancer-sans-exe.ps1
echo        (compile le programme en memoire, sans produire de .exe).
echo.
pause
exit /b 2

:compiler
echo Compilation de GpuMonitor.exe ...
if exist "%CSC64%" (
  "%CSC64%" /nologo /platform:anycpu /target:winexe /optimize+ /out:GpuMonitor.exe GpuMonitor.cs /r:System.dll,System.Core.dll,System.Windows.Forms.dll,System.Drawing.dll,System.Management.dll,System.Windows.Forms.DataVisualization.dll
) else (
  "%CSC32%" /nologo /platform:anycpu /target:winexe /optimize+ /out:GpuMonitor.exe GpuMonitor.cs /r:System.dll,System.Core.dll,System.Windows.Forms.dll,System.Drawing.dll,System.Management.dll,System.Windows.Forms.DataVisualization.dll
)

if not exist "GpuMonitor.exe" (
  echo.
  echo ECHEC de la compilation.
  echo.
  pause
  exit /b 1
)

echo.
echo GpuMonitor.exe cree avec succes.
endlocal
exit /b 0
