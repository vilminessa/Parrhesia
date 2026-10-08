# ============================================================
# Parrhesia — установка/удаление устройств драйвера (Ф5).
# Запускается инсталлятором от администратора.
#   без параметров      — установка: пакет INF +4 devnode (base+lanes)
#   -Uninstall          — снятие devnode'ов (пакет остаётся)
#
# devcon НЕ поставляется: используется SetupAPI-эквивалент
# (SetupDiCreateDeviceInfo + DIF_REGISTERDEVICE +
#  UpdateDriverForPlugAndPlayDevices).
# ============================================================
param([switch]$Uninstall)

$ErrorActionPreference = 'Continue'
$hwids = @(
    'ROOT\VirtualAudioDriver',
    'ROOT\ParrhesiaLane1',
    'ROOT\ParrhesiaLane2',
    'ROOT\ParrhesiaLane3'
)
$log = "$env:TEMP\parrhesia-installer.log"
Set-Content $log "=== install-devices $(Get-Date -Format o) args=$args ===" -Encoding UTF8

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;

public static class ParrSetup
{
    [StructLayout(LayoutKind.Sequential)]
    public struct SP_DEVINFO_DATA
    {
        public int cbSize;
        public Guid ClassGuid;
        public int DevInst;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", SetLastError = true)]
    public static extern IntPtr SetupDiCreateDeviceInfoList(ref Guid classGuid, IntPtr hwndParent);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool SetupDiCreateDeviceInfo(
        IntPtr devInfoSet, string deviceName, ref Guid classGuid, string description,
        IntPtr hwndParent, uint flags, ref SP_DEVINFO_DATA deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    public static extern bool SetupDiSetDeviceRegistryProperty(
        IntPtr devInfoSet, ref SP_DEVINFO_DATA deviceInfoData, uint property,
        byte[] propertyBuffer, uint propertyBufferSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    public static extern bool SetupDiCallClassInstaller(
        uint installFunction, IntPtr devInfoSet, ref SP_DEVINFO_DATA deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    public static extern bool SetupDiDestroyDeviceInfoList(IntPtr devInfoSet);

    [DllImport("setupapi.dll", SetLastError = true)]
    public static extern bool SetupDiEnumDeviceInfo(IntPtr devInfoSet, uint memberIndex, ref SP_DEVINFO_DATA deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    public static extern bool SetupDiGetDeviceRegistryProperty(
        IntPtr devInfoSet, ref SP_DEVINFO_DATA deviceInfoData, uint property,
        out uint propertyRegType, byte[] propertyBuffer, uint propertyBufferSize, out uint requiredSize);

    [DllImport("newdev.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool UpdateDriverForPlugAndPlayDevices(
        IntPtr hwndParent, string hardwareId, string fullInfPath, uint installFlags,
        out bool rebootRequired);

    public const uint DICD_GENERATE_ID = 0x00000001;
    public const uint SPDRP_HARDWAREID = 1;
    public const uint DIF_REGISTERDEVICE = 0x00000019;
    public const uint DIF_REMOVE = 0x00000002;
    public const uint INSTALLFLAG_FORCE = 0x1;
}
"@ -ErrorAction Stop

function Remove-DeviceByHwid([string]$hwid) {
    # Реестр Enum => instance-id (ROOT\MEDIA\000N) => pnputil /remove-device.
    $instances = @()
    Get-ChildItem "HKLM:\SYSTEM\CurrentControlSet\Enum\ROOT" -Recurse -ErrorAction SilentlyContinue | ForEach-Object {
        $p = Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue
        if ($p -and (($p.HardwareID -is [array] -and ($p.HardwareID -contains $hwid)) -or
                     ($p.HardwareID -is [string] -and $p.HardwareID -eq $hwid))) {
            $instances += ($_.Name -replace '^HKEY_LOCAL_MACHINE\\', '')
        }
    }
    if ($instances.Count -eq 0) {
        "  ${hwid}: не найден" | Add-Content $log
        return
    }
    foreach ($inst in $instances) {
        pnputil /remove-device "$inst" 2>&1 | Add-Content $log
        "  ${hwid}: снятие $inst" | Add-Content $log
    }
}

function Test-DeviceExists([string]$hwid) {
    # Реестр Enum => HardwareID (REG_MULTI_SZ) — надёжнее SetupAPI-обхода.
    foreach ($root in (Get-ChildItem "HKLM:\SYSTEM\CurrentControlSet\Enum\ROOT" -Recurse -ErrorAction SilentlyContinue)) {
        $p = Get-ItemProperty $root.PSPath -ErrorAction SilentlyContinue
        if ($null -eq $p) { continue }
        if ($p.HardwareID -is [array] -and ($p.HardwareID -contains $hwid)) { return $true }
        if ($p.HardwareID -is [string] -and $p.HardwareID -eq $hwid) { return $true }
    }
    return $false
}

function Create-Device([string]$hwid, [string]$desc, [string]$infPath) {
    $classGuid = [Guid]'{4d36e96c-e325-11ce-bfc1-08002be10318}'  # MEDIA
    $list = [ParrSetup]::SetupDiCreateDeviceInfoList([ref]$classGuid, [IntPtr]::Zero)
    if ($list -eq [IntPtr]::Zero) { "  ${hwid}: CreateDeviceInfoList failed $([ComponentModel.Win32Exception]::new([Runtime.InteropServices.Marshal]::GetLastWin32Error()).Message)" | Add-Content $log; return }

    $data = New-Object ParrSetup+SP_DEVINFO_DATA
    $data.cbSize = [Runtime.InteropServices.Marshal]::SizeOf($data)
    if (-not [ParrSetup]::SetupDiCreateDeviceInfo($list, $hwid, [ref]$classGuid, $desc, [IntPtr]::Zero, [ParrSetup]::DICD_GENERATE_ID, [ref]$data)) {
        "  ${hwid}: CreateDeviceInfo failed $([ComponentModel.Win32Exception]::new([Runtime.InteropServices.Marshal]::GetLastWin32Error()).Message)" | Add-Content $log
        [ParrSetup]::SetupDiDestroyDeviceInfoList($list) | Out-Null
        return
    }

    $bytes = [System.Text.Encoding]::Unicode.GetBytes($hwid + "`0`0")
    [ParrSetup]::SetupDiSetDeviceRegistryProperty($list, [ref]$data, [ParrSetup]::SPDRP_HARDWAREID, $bytes, $bytes.Length) | Out-Null
    [ParrSetup]::SetupDiCallClassInstaller([ParrSetup]::DIF_REGISTERDEVICE, $list, [ref]$data) | Out-Null
    [ParrSetup]::SetupDiDestroyDeviceInfoList($list) | Out-Null

    $reboot = $false
    if ([ParrSetup]::UpdateDriverForPlugAndPlayDevices([IntPtr]::Zero, $hwid, $infPath, [ParrSetup]::INSTALLFLAG_FORCE, [ref]$reboot)) {
        "  ${hwid}: установлен (reboot=$reboot)" | Add-Content $log
    } else {
        "  ${hwid}: UpdateDriver failed $([ComponentModel.Win32Exception]::new([Runtime.InteropServices.Marshal]::GetLastWin32Error()).Message)" | Add-Content $log
    }
}

if ($Uninstall) {
    "--- снятие устройств ---" | Add-Content $log
    foreach ($hw in $hwids) { Remove-DeviceByHwid $hw }
    "done" | Add-Content $log
    exit 0
}

# --- установка ---
$pkg = "$PSScriptRoot\driver-package"
if (-not (Test-Path "$pkg\VirtualAudioDriver.inf")) {
    # раскладка репозитория (запуск из installer\)
    $pkg = "$PSScriptRoot\..\driver\x64\Release\package"
}
if (-not (Test-Path "$pkg\VirtualAudioDriver.inf")) {
    "ПАКЕТ НЕ НАЙДЕН: ни $PSScriptRoot\driver-package, ни ..\driver\x64\Release\package" | Add-Content $log
    exit 1
}

"--- пакет драйвера (pnputil) ---" | Add-Content $log
pnputil /add-driver "$pkg\VirtualAudioDriver.inf" /install 2>&1 | Add-Content $log

"--- devnode'ы ---" | Add-Content $log
$descs = @{
    'ROOT\VirtualAudioDriver' = 'Parrhesia'
    'ROOT\ParrhesiaLane1' = 'Parrhesia L1'
    'ROOT\ParrhesiaLane2' = 'Parrhesia L2'
    'ROOT\ParrhesiaLane3' = 'Parrhesia L3'
}
$inf = "$pkg\VirtualAudioDriver.inf"
foreach ($hw in $hwids) {
    if (Test-DeviceExists $hw) {
        "  ${hw}: уже установлен — пропуск" | Add-Content $log
        continue
    }
    Create-Device $hw $descs[$hw] $inf
}
"done" | Add-Content $log
exit 0
