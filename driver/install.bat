@echo off
rem ============================================================
rem  Parrhesia - установка виртуального аудио-драйвера (Э0)
rem  Требуется: администратор + testsigning ON (bcdedit /set
rem  testsigning on + перезагрузка - один раз за жизнь системы)
rem ============================================================
setlocal
chcp 65001 >nul
set "PKG=%~dp0x64\Release\package"
set "DEVCON=%ProgramFiles(x86)%\Windows Kits\10\Tools\10.0.26100.0\x64\devcon.exe"

net session >nul 2>&1
if errorlevel 1 goto :need_admin
if not exist "%PKG%\VirtualAudioDriver.inf" goto :no_package
if not exist "%DEVCON%" goto :no_devcon

echo Установка пакета и создание device instances (base + lanes)...
"%DEVCON%" install "%PKG%\VirtualAudioDriver.inf" ROOT\VirtualAudioDriver
if errorlevel 1 goto :install_failed

rem Лanes (М2): каждый HW-ID = отдельный devnode со своей парой endpoints.
rem Ошибка лана не фатальна — база уже установлена.
"%DEVCON%" install "%PKG%\VirtualAudioDriver.inf" ROOT\ParrhesiaLane1
if errorlevel 1 echo [warn] Lane1 не установился
"%DEVCON%" install "%PKG%\VirtualAudioDriver.inf" ROOT\ParrhesiaLane2
if errorlevel 1 echo [warn] Lane2 не установился
"%DEVCON%" install "%PKG%\VirtualAudioDriver.inf" ROOT\ParrhesiaLane3
if errorlevel 1 echo [warn] Lane3 не установился

echo.
echo Готово. В "Звуке" должны появиться "Parrhesia In" и "Parrhesia Out".
exit /b 0

:need_admin
echo Запустите install.bat от имени администратора.
exit /b 1

:no_package
echo Пакет не найден: %PKG%\VirtualAudioDriver.inf
echo Сначала соберите драйвер: build.bat
exit /b 1

:no_devcon
echo devcon.exe не найден: %DEVCON%
exit /b 1

:install_failed
echo Установка не удалась. Проверьте testsigning ^(bcdedit /enum^).
exit /b 1
