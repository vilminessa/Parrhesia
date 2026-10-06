@echo off
setlocal
chcp 65001 >nul
title Parrhesia - консоль проекта
cd /d "%~dp0"
set "EXE=src\Parrhesia.App\bin\Debug\net10.0-windows\Parrhesia.App.exe"
set "SLN=Parrhesia.slnx"
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
set "RETURN=menu"

rem Быстрый режим: debug.bat <номер пункта> — выполнить и выйти (для скриптов).
if "%~1"=="" goto menushow
set "sel=%~1"
set "RETURN=bye"
goto dispatch

:menushow
cls
echo ============================================================
echo    PARRHESIA - консоль управления проектом
echo    %CD%
echo ============================================================
echo.
echo    [1] Собрать решение
echo    [2] Запустить тесты
echo    [3] Запустить приложение
echo    [4] Запустить с логами в эту консоль
echo    [5] Запустить «Схему» с логами
echo    [6] Собрать и запустить с логами
echo    [7] Очистить bin/obj
echo    [8] Git: статус и история
echo    [9] Открыть папку профилей
echo    [0] Выход
echo.
set "sel="
set /p "sel=Выбор: " || exit /b 0

:dispatch
if "%sel%"=="1" goto build
if "%sel%"=="2" goto test
if "%sel%"=="3" goto run
if "%sel%"=="4" goto runlogs
if "%sel%"=="5" goto runschema
if "%sel%"=="6" goto buildrun
if "%sel%"=="7" goto clean
if "%sel%"=="8" goto git
if "%sel%"=="9" goto profiles
if "%sel%"=="0" goto bye
goto menushow

:build
echo.
echo --- Сборка ---
dotnet build "%SLN%" --nologo
goto hold

:test
echo.
echo --- Тесты ---
dotnet test "%SLN%" --nologo
goto hold

:run
if not exist "%EXE%" (
    echo Сборка не найдена, собираю...
    dotnet build "%SLN%" --nologo
)
if not exist "%EXE%" goto hold
start "" "%EXE%"
goto hold

:runlogs
if not exist "%EXE%" (
    echo Сборка не найдена, собираю...
    dotnet build "%SLN%" --nologo
)
if not exist "%EXE%" goto hold
echo.
echo --- Запуск с логами (закройте приложение, чтобы вернуться) ---
start "" "%EXE%" --console
goto hold

:runschema
if not exist "%EXE%" (
    echo Сборка не найдена, собираю...
    dotnet build "%SLN%" --nologo
)
if not exist "%EXE%" goto hold
echo.
echo --- Запуск «Схемы» с логами (закройте приложение, чтобы вернуться) ---
start "" "%EXE%" --tab 1 --console
goto hold

:buildrun
echo.
echo --- Сборка ---
dotnet build "%SLN%" --nologo
if errorlevel 1 goto hold
echo.
echo --- Запуск с логами (закройте приложение, чтобы вернуться) ---
start "" "%EXE%" --console
goto hold

:clean
echo.
echo --- Очистка bin/obj ---
for %%p in (
    src\Parrhesia.Core
    src\Parrhesia.Audio
    src\Parrhesia.App
    tests\Parrhesia.Core.Tests
    tests\Parrhesia.Audio.Tests
) do (
    if exist "%%p\bin" rd /s /q "%%p\bin"
    if exist "%%p\obj" rd /s /q "%%p\obj"
)
echo Готово.
goto hold

:git
echo.
echo --- git status ---
git status --short --branch
echo.
echo --- последние коммиты ---
git log --oneline -10
goto hold

:profiles
explorer "%APPDATA%\Parrhesia\profiles"
goto menushow

:hold
echo.
if "%RETURN%"=="bye" goto bye
pause
goto menushow

:bye
endlocal
exit /b 0
