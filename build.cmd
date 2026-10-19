@echo off
rem Builds MonitorSwitch.exe with the C# compiler bundled in .NET Framework 4.x (no SDK needed).
setlocal
cd /d "%~dp0"
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (echo .NET Framework 4.x C# compiler not found & exit /b 1)
taskkill /im MonitorSwitch.exe /f >nul 2>&1
"%CSC%" /nologo /codepage:65001 /optimize+ /warn:4 /warnaserror+ /target:winexe /out:MonitorSwitch.exe ^
  /r:System.Management.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /recurse:src\*.cs || exit /b 1
echo Built %~dp0MonitorSwitch.exe
