@echo off
rem ============================================================
rem  ?????? ????????? VST3-??????? Parrhesia (?? ???? vendor/vst3sdk,
rem  MIT). ?????: out\test-plugin-vst3.dll
rem ============================================================
setlocal
chcp 65001 >nul
set "HERE=%~dp0"
set "SDK=%HERE%..\..\..\src\Parrhesia.Plugins\vendor\vst3sdk"
set "VCVARS=%ProgramFiles(x86)%\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat"

if not exist "%VCVARS%" call :find_vcvars
if not exist "%VCVARS%" goto :no_vcvars
call "%VCVARS%" >nul
if errorlevel 1 goto :vcvars_failed

if not exist "%HERE%out" mkdir "%HERE%out"

cl /nologo /LD /O2 /EHsc /std:c++17 /MT /DRELEASE=1 /DUNICODE /D_UNICODE ^
    /I "%SDK%" ^
    "%HERE%testplugin.cpp" ^
    "%SDK%\public.sdk\source\main\pluginfactory.cpp" ^
    "%SDK%\public.sdk\source\main\moduleinit.cpp" ^
    "%SDK%\public.sdk\source\main\dllmain.cpp" ^
    "%SDK%\public.sdk\source\vst\vstsinglecomponenteffect.cpp" ^
    "%SDK%\public.sdk\source\vst\vstcomponentbase.cpp" ^
    "%SDK%\public.sdk\source\vst\vstcomponent.cpp" ^
    "%SDK%\public.sdk\source\vst\vstbus.cpp" ^
    "%SDK%\public.sdk\source\vst\vstparameters.cpp" ^
    "%SDK%\public.sdk\source\vst\vstinitiids.cpp" ^
    "%SDK%\public.sdk\source\vst\vstaudioeffect.cpp" ^
    "%SDK%\public.sdk\source\common\commoniids.cpp" ^
    "%SDK%\public.sdk\source\common\memorystream.cpp" ^
    "%SDK%\public.sdk\source\common\commonstringconvert.cpp" ^
    "%SDK%\base\thread\source\flock.cpp" ^
    "%SDK%\base\thread\source\fcondition.cpp" ^
    "%SDK%\public.sdk\source\vst\utility\stringconvert.cpp" ^
    "%SDK%\pluginterfaces\base\funknown.cpp" ^
    "%SDK%\pluginterfaces\base\coreiids.cpp" ^
    "%SDK%\pluginterfaces\base\ustring.cpp" ^
    "%SDK%\pluginterfaces\base\conststringtable.cpp" ^
    "%SDK%\public.sdk\source\common\pluginview.cpp" ^    "%SDK%\base\source\baseiids.cpp" ^
    "%SDK%\base\source\fbuffer.cpp" ^
    "%SDK%\base\source\fdebug.cpp" ^
    "%SDK%\base\source\fdynlib.cpp" ^
    "%SDK%\base\source\fobject.cpp" ^
    "%SDK%\base\source\fstreamer.cpp" ^
    "%SDK%\base\source\fstring.cpp" ^
    "%SDK%\base\source\timer.cpp" ^
    "%SDK%\base\source\updatehandler.cpp" ^
    /Fo"%HERE%out\\" ^
    /Fe"%HERE%out\test-plugin-vst3.dll" ^
    /link /DEF:"%HERE%testplugin.def" ole32.lib user32.lib ole32.lib user32.lib
if errorlevel 1 goto :compile_failed

echo ??????: %HERE%out\test-plugin-vst3.dll
exit /b 0

:no_vcvars
echo vcvars64.bat ?? ??????: %VCVARS%
exit /b 1

:vcvars_failed
echo ?????? vcvars64.
exit /b 1

:compile_failed
echo ?????? VST3-????-??????? ?? ???????.
exit /b 1
rem --- fallback: vswhere, ???? BuildTools 2022 ?? ?????? (GitHub runner / VS Community)
:find_vcvars
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%VSWHERE%" exit /b 0
for /f "usebackq tokens=*" %%i in (`"%VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VSROOT=%%i"
if defined VSROOT set "VCVARS=%VSROOT%\VC\Auxiliary\Build\vcvars64.bat"
exit /b 0
