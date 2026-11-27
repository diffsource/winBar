@echo off
rem Builds TaskbarTap.dll (x64, static CRT so explorer needs no VC runtime).
setlocal enabledelayedexpansion
rem Delayed expansion: the ")" in "Program Files (x86)" would break the for-block otherwise.
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
set "VS="
for /f "usebackq delims=" %%i in (`"!VSWHERE!" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VS=%%i"
if not defined VS (echo MSVC not found & exit /b 1)
call "%VS%\VC\Auxiliary\Build\vcvars64.bat" >nul 2>&1 || exit /b 1
cd /d "%~dp0"
cl /nologo /LD /MT /EHsc /O2 /std:c++20 /permissive- TaskbarTap.cpp /Fe:TaskbarTap.dll /link windowsapp.lib user32.lib ole32.lib || exit /b 1
del TaskbarTap.obj TaskbarTap.lib TaskbarTap.exp 2>nul
