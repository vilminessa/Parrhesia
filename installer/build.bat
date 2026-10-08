@echo off
rem ============================================================
rem  Parrhesia - сборка инсталлятора (Ф5)
rem  1) dotnet publish приложения (self-contained win-x64)
rem  2) драйвер (если не собран)
rem  3) ISCC - компиляция installer\Parrhesia.iss → dist\
rem ============================================================
chcp 65001 >nul
setlocal
set "HERE=%~dp0"
set "ROOT=%HERE%.."

rem --- версия: b*/v* тег или fallback0.1.0
set "VER=0.1.0"
for /f "delims=" %%v in ('git -C "%ROOT%" describe --tags --exact-match 2^>nul') do set "VER=%%v"
if "%VER:~0,1%"=="b" set "VER=%VER:~1%"
if "%VER:~0,1%"=="v" set "VER=%VER:~1%"

echo [1/3] dotnet publish (win-x64, self-contained), version %VER%...
dotnet publish "%ROOT%\src\Parrhesia.App\Parrhesia.App.csproj" -c Release -r win-x64 --self-contained true -o "%ROOT%\publish\app" /p:Version=%VER% --nologo -v minimal
if errorlevel 1 goto :fail

echo [2/3] драйвер...
if exist "%ROOT%\driver\x64\Release\package\VirtualAudioDriver.inf" (
  echo   пакет уже собран
) else (
  call "%ROOT%\driver\build.bat"
  if errorlevel 1 goto :fail
)

set "ISCC=%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe"
if not exist "%ISCC%" set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
if not exist "%ISCC%" set "ISCC=%ProgramFiles%\Inno Setup 6\ISCC.exe"
if not exist "%ISCC%" (
  echo ISCC.exe не найден - установите Inno Setup 6
  goto :fail
)

echo [3/3] ISCC...
"%ISCC%" /DMyAppVersion=%VER% "%HERE%Parrhesia.iss"
if errorlevel 1 goto :fail

echo.
echo Готово: "%ROOT%\dist\Parrhesia-Setup-%VER%.exe"
exit /b 0

:fail
echo СБОРКА ПРОВАЛЕНА
exit /b 1
