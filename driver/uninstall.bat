@echo off
rem ============================================================
rem  Parrhesia - удаление виртуального аудио-драйвера (админ)
rem  Перезагрузка обычно НЕ требуется (devnode удаляется на лету)
rem ============================================================
setlocal
chcp 65001 >nul
set "DEVCON=%ProgramFiles(x86)%\Windows Kits\10\Tools\10.0.26100.0\x64\devcon.exe"

net session >nul 2>&1
if errorlevel 1 goto :need_admin

echo Удаление device instances (base + lanes1..7)...
"%DEVCON%" remove ROOT\VirtualAudioDriver
"%DEVCON%" remove ROOT\ParrhesiaLane1
"%DEVCON%" remove ROOT\ParrhesiaLane2
"%DEVCON%" remove ROOT\ParrhesiaLane3
"%DEVCON%" remove ROOT\ParrhesiaLane4
"%DEVCON%" remove ROOT\ParrhesiaLane5
"%DEVCON%" remove ROOT\ParrhesiaLane6
"%DEVCON%" remove ROOT\ParrhesiaLane7

echo.
echo Опубликованные пакеты драйверов с нашим INF:
pnputil /enum-drivers | findstr /i "VirtualAudioDriver"
echo Если видите published name ^(oemNN.inf^) - удалите вручную:
echo   pnputil /delete-driver oemNN.inf /uninstall /force
exit /b 0

:need_admin
echo Запустите uninstall.bat от имени администратора.
exit /b 1
