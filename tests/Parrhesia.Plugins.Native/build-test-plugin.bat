@echo off
rem ============================================================
rem  ?????? ???????? CLAP-???????? Parrhesia (C, header-only CLAP).
rem  ????????? MSVC (VS Build Tools 2022 + workload VCTools).
rem  ?????: out\test-plugin.dll
rem ============================================================
setlocal
chcp 65001 >nul
set "HERE=%~dp0"
set "VCVARS=%ProgramFiles(x86)%\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat"

if not exist "%VCVARS%" goto :no_vcvars
call "%VCVARS%" >nul
if errorlevel 1 goto :vcvars_failed

if not exist "%HERE%out" mkdir "%HERE%out"

cl /nologo /LD /O2 /W3 /TC /I "%HERE%..\..\src\Parrhesia.Plugins\vendor\clap\include" "%HERE%test-plugin.c" /Fo"%HERE%out\\" /Fe"%HERE%out\test-plugin.dll" /link user32.lib
if errorlevel 1 goto :compile_failed

echo ??????: %HERE%out\test-plugin.dll
exit /b 0

:no_vcvars
echo vcvars64.bat ?? ??????: %VCVARS%
exit /b 1

:vcvars_failed
echo ?????? vcvars64.
exit /b 1

:compile_failed
echo ?????? ????-??????? ?? ???????.
exit /b 1
