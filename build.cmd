@echo off
rem Builds MonitorSwitch.exe with the C# compiler bundled in .NET Framework 4.x (no SDK needed).
setlocal
cd /d "%~dp0"
taskkill /im MonitorSwitch.exe /f >nul 2>&1
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /codepage:65001 /optimize+ /target:winexe /out:MonitorSwitch.exe /r:System.Management.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll MonitorSwitch.cs || exit /b 1
echo Built %~dp0MonitorSwitch.exe
