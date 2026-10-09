@echo off
chcp 65001 >nul
rem ============================================================
rem  Parrhesia - сборка виртуального аудио-драйвера (Ф4)
rem  Требования: VS Build Tools 2022 (workload VCTools)
rem               WDK 10.0.26100 (Microsoft.WindowsWDK.10.0.26100)
rem  Регистрация тулсета WindowsKernelModeDriver10.0 выполняется один раз:
rem      register-toolset.ps1 (см. README драйвера)
rem ============================================================
setlocal
set MSBUILD=%ProgramFiles(x86)%\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe
if exist "%MSBUILD%" goto :run
set MSBUILD=%ProgramFiles%\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe
if exist "%MSBUILD%" goto :run
echo MSBuild (VS Build Tools 2022) не найден.
echo Установите Visual Studio Build Tools с workload "C++ build tools".
exit /b 1

:run
"%MSBUILD%" "%~dp0VirtualAudioDriver.sln" %*
exit /b %ERRORLEVEL%
