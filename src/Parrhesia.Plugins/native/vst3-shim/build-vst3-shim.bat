@echo off
rem ============================================================
rem  ?????? Parrhesia VST3 Shim (C-API ????? ?? vendor/vst3sdk).
rem  ?????: out\parr_vst3_shim.dll
rem ============================================================
setlocal
chcp 65001 >nul
set "HERE=%~dp0"
set "SDK=%HERE%..\..\vendor\vst3sdk"
set "VCVARS=%ProgramFiles(x86)%\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat"

if not exist "%VCVARS%" goto :no_vcvars
call "%VCVARS%" >nul
if errorlevel 1 goto :vcvars_failed

if not exist "%HERE%out" mkdir "%HERE%out"

cl /nologo /LD /O2 /EHsc /std:c++17 /MT /DRELEASE=1 /DUNICODE /D_UNICODE ^
    /I "%SDK%" ^
    "%HERE%vst3shim.cpp" ^
    "%SDK%\public.sdk\source\vst\hosting\module.cpp" ^
    "%SDK%\public.sdk\source\vst\hosting\module_win32.cpp" ^
    "%SDK%\public.sdk\source\vst\hosting\plugprovider.cpp" ^
    "%SDK%\public.sdk\source\vst\hosting\pluginterfacesupport.cpp" ^
    "%SDK%\public.sdk\source\vst\hosting\hostclasses.cpp" ^
    "%SDK%\public.sdk\source\vst\hosting\connectionproxy.cpp" ^
    "%SDK%\public.sdk\source\vst\hosting\processdata.cpp" ^
    "%SDK%\public.sdk\source\vst\hosting\parameterchanges.cpp" ^
    "%SDK%\public.sdk\source\vst\hosting\eventlist.cpp" ^
    "%SDK%\public.sdk\source\vst\vstinitiids.cpp" ^
    "%SDK%\public.sdk\source\vst\utility\stringconvert.cpp" ^
    "%SDK%\public.sdk\source\common\commoniids.cpp" ^
    "%SDK%\public.sdk\source\common\commonstringconvert.cpp" ^
    "%SDK%\public.sdk\source\common\memorystream.cpp" ^
    "%SDK%\public.sdk\source\common\pluginview.cpp" ^
    "%SDK%\public.sdk\source\common\threadchecker_win32.cpp" ^
    "%SDK%\pluginterfaces\base\funknown.cpp" ^
    "%SDK%\pluginterfaces\base\coreiids.cpp" ^
    "%SDK%\pluginterfaces\base\ustring.cpp" ^
    "%SDK%\pluginterfaces\base\conststringtable.cpp" ^
    "%SDK%\base\source\baseiids.cpp" ^
    "%SDK%\base\source\fbuffer.cpp" ^
    "%SDK%\base\source\fdebug.cpp" ^
    "%SDK%\base\source\fdynlib.cpp" ^
    "%SDK%\base\source\fobject.cpp" ^
    "%SDK%\base\source\fstreamer.cpp" ^
    "%SDK%\base\source\fstring.cpp" ^
    "%SDK%\base\source\timer.cpp" ^
    "%SDK%\base\source\updatehandler.cpp" ^
    "%SDK%\base\thread\source\flock.cpp" ^
    "%SDK%\base\thread\source\fcondition.cpp" ^
    /Fo"%HERE%out\\" ^
    /Fe"%HERE%out\parr_vst3_shim.dll" ^
    /link ole32.lib user32.lib
if errorlevel 1 goto :compile_failed

echo ??????: %HERE%out\parr_vst3_shim.dll
exit /b 0

:no_vcvars
echo vcvars64.bat ?? ??????: %VCVARS%
exit /b 1

:vcvars_failed
echo ?????? vcvars64.
exit /b 1

:compile_failed
echo ?????? VST3-???? ?? ???????.
exit /b 1
