# ============================================================
# Parrhesia — установка/удаление устройств драйвера (Ф5/В1).
# Запускается инсталлятором или приложением (менеджер кабелей)
# от администратора.
#   без параметров          — установка пакета INF +4 devnode (base+lanes)
#                             + rebind всех устройств на свежий пакет
#                             + чистка устаревших oem*.inf в driver store
#   -Uninstall              — снятие ВСЕХ devnode (base + все Lane*)
#   -Add -Hwid <id> [-Desc] — установка одного devnode живьём (В1, без ребута)
#   -Remove -Hwid <id>      — снятие одного devnode живьём (В1, без ребута)
#
# devcon НЕ поставляется: SetupAPI (SetupDiCreateDeviceInfo с ПОЛНЫМ
# instance-id "ROOT\MEDIA\NNNN", CreationFlags=0) +
# UpdateDriverForPlugAndPlayDevices. Журнал В1 (спайк): удаление —0.6 с,
# создание —0.5 с, reboot=False во всех случаях.
#
# ВАЖНО: SetupDiSetDeviceRegistryProperty ТОЛЬКО CharSet.Unicode —
# A-вариант читает UTF-16 как ANSI и портит MULTI_SZ HardwareID
# (получается19 строк) → UpdateDriver падает SPAPI_E_NO_SUCH_DEVINST.
# ============================================================
param(
    [switch]$Uninstall,
    [switch]$Add,
    [switch]$Remove,
    [string]$Hwid = '',
    [string]$Desc = ''
)

$ErrorActionPreference = 'Continue'
$hwids = @(
    'ROOT\VirtualAudioDriver',
    'ROOT\ParrhesiaLane1',
    'ROOT\ParrhesiaLane2',
    'ROOT\ParrhesiaLane3'
)
$log = "$env:TEMP\parrhesia-installer.log"
Set-Content $log "=== install-devices $(Get-Date -Format o) args=$($args -join ' ') Add=$Add Remove=$Remove Hwid=$Hwid Uninstall=$Uninstall ===" -Encoding UTF8

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

    // CharSet.Unicode ОБЯЗАТЕЛЕН: без него вызывается A-вариант и
    // MULTI_SZ HardwareID портится (см. шапку файла).
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool SetupDiSetDeviceRegistryProperty(
        IntPtr devInfoSet, ref SP_DEVINFO_DATA deviceInfoData, uint property,
        byte[] propertyBuffer, uint propertyBufferSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    public static extern bool SetupDiCallClassInstaller(
        uint installFunction, IntPtr devInfoSet, ref SP_DEVINFO_DATA deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    public static extern bool SetupDiDestroyDeviceInfoList(IntPtr devInfoSet);

    [DllImport("newdev.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool UpdateDriverForPlugAndPlayDevices(
        IntPtr hwndParent, string hardwareId, string fullInfPath, uint installFlags,
        out bool rebootRequired);

    // Дети devnode'а (DEVPKEY_Device_Children): endpoint'ы SWD\MMDEVAPI\*.
    // pnputil /remove-device НЕ каскадит детей — их надо снимать отдельно,
    // иначе остаются state=1 призраки (журнал В1: валидация add/remove).
    [StructLayout(LayoutKind.Sequential)]
    public struct DEVPROPKEY { public Guid fmtid; public uint pid; }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    public static extern int CM_Locate_DevNodeW(out uint pdwDevInst, string pDeviceID, uint ulFlags);

    [DllImport("cfgmgr32.dll")]
    public static extern int CM_Get_DevNode_PropertyW(
        uint dnDevInst, ref DEVPROPKEY propertyKey, out uint propertyType,
        IntPtr propertyData, ref uint dataSize, uint ulFlags);

    public static string[] GetChildren(string instanceId)
    {
        uint devInst;
        if (CM_Locate_DevNodeW(out devInst, instanceId, 0) != 0)
        {
            return new string[0];
        }

        var key = new DEVPROPKEY
        {
            fmtid = new Guid("4340a6c5-93fa-4706-972c-7b648008a5a7"), // DEVPKEY_Device_Children
            pid = 9
        };

        uint size = 0, type;
        CM_Get_DevNode_PropertyW(devInst, ref key, out type, IntPtr.Zero, ref size, 0);
        if (size == 0)
        {
            return new string[0];
        }

        IntPtr buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (CM_Get_DevNode_PropertyW(devInst, ref key, out type, buf, ref size, 0) != 0)
            {
                return new string[0];
            }

            var list = new System.Collections.Generic.List<string>();
            int offset = 0;
            while (offset + 1 < size)
            {
                string value = Marshal.PtrToStringUni(buf + offset);
                if (string.IsNullOrEmpty(value))
                {
                    break;
                }

                list.Add(value);
                offset += (value.Length + 1) * 2;
            }

            return list.ToArray();
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    public const uint DICD_GENERATE_ID = 0x00000001; // НЕ использовать: hwid — не instance-id
    public const uint SPDRP_HARDWAREID = 1;
    public const uint DIF_REGISTERDEVICE = 0x00000019;
    public const uint INSTALLFLAG_FORCE = 0x1;
}
"@ -ErrorAction Stop

# Путь реестра → короткий instance-id (ROOT\MEDIA\000N) для pnputil.
function Get-ShortInstanceId([string]$regName) {
    return ($regName -replace '^HKEY_LOCAL_MACHINE\\SYSTEM\\CurrentControlSet\\Enum\\', '')
}

# Все instance-id для hwid (обычно один).
function Find-Instances([string]$hwid) {
    $result = @()
    foreach ($root in (Get-ChildItem "HKLM:\SYSTEM\CurrentControlSet\Enum\ROOT" -Recurse -ErrorAction SilentlyContinue)) {
        $p = Get-ItemProperty $root.PSPath -ErrorAction SilentlyContinue
        if ($null -eq $p) { continue }
        $hit = ($p.HardwareID -is [array] -and $p.HardwareID -contains $hwid) -or
               ($p.HardwareID -is [string] -and $p.HardwareID -eq $hwid)
        if ($hit) { $result += (Get-ShortInstanceId $root.Name) }
    }
    return $result
}

function Test-DeviceExists([string]$hwid) {
    return (Find-Instances $hwid).Count -gt 0
}

# Живое снятие: сначала endpoint-дети (пнпутл не каскадит их — иначе
# призраки state=1), затем сам devnode. Короткий instance-id обязателен
# (полный путь "SYSTEM\CurrentControlSet\Enum\..." pnputil не принимает —
# баг Ф5: удаление молча не срабатывало, маскировалось идемпотентностью).
function Remove-DeviceByHwid([string]$hwid) {
    $instances = Find-Instances $hwid
    if ($instances.Count -eq 0) {
        "  ${hwid}: не найден" | Add-Content $log
        return
    }
    foreach ($inst in $instances) {
        # endpoint-дети до удаления родителя (пока дерево ещё читается)
        $children = [ParrSetup]::GetChildren($inst)
        foreach ($c in $children) {
            $cout = pnputil /remove-device "$c" 2>&1
            "    child ${c} (exit=$LASTEXITCODE)" | Add-Content $log
        }
        $out = pnputil /remove-device "$inst" 2>&1
        $rc = $LASTEXITCODE
        "  ${hwid}: снятие ${inst} (exit=$rc)" | Add-Content $log
        $out | ForEach-Object { "    $_" | Add-Content $log }
    }
}

# Свободный slot ROOT\MEDIA\NNNN (сканирует занятые).
function Get-FreeMediaInstanceId {
    $used = @()
    foreach ($k in (Get-ChildItem "HKLM:\SYSTEM\CurrentControlSet\Enum\ROOT\MEDIA" -ErrorAction SilentlyContinue)) {
        if ($k.PSChildName -match '^\d{4}$') { $used += [int]$k.PSChildName }
    }
    $n = 0
    while ($used -contains $n) { $n++ }
    return 'ROOT\MEDIA\{0:D4}' -f $n
}

# Живое создание: полный instance-id + Flags=0 (INVALID_DEVINST_NAME при
# DICD_GENERATE_ID с hwid — баг Ф5, см. шапку).
function Create-Device([string]$hwid, [string]$desc, [string]$infPath) {
    $instNew = Get-FreeMediaInstanceId
    $classGuid = [Guid]'{4d36e96c-e325-11ce-bfc1-08002be10318}'  # MEDIA
    $list = [ParrSetup]::SetupDiCreateDeviceInfoList([ref]$classGuid, [IntPtr]::Zero)
    if ($list -eq [IntPtr]::Zero) {
        "  ${hwid}: CreateDeviceInfoList failed $([ComponentModel.Win32Exception]::new([Runtime.InteropServices.Marshal]::GetLastWin32Error()).Message)" | Add-Content $log
        return
    }

    $data = New-Object ParrSetup+SP_DEVINFO_DATA
    $data.cbSize = [Runtime.InteropServices.Marshal]::SizeOf($data)
    if (-not [ParrSetup]::SetupDiCreateDeviceInfo($list, $instNew, [ref]$classGuid, $desc, [IntPtr]::Zero, 0, [ref]$data)) {
        "  ${hwid}: CreateDeviceInfo($instNew) failed $([ComponentModel.Win32Exception]::new([Runtime.InteropServices.Marshal]::GetLastWin32Error()).Message)" | Add-Content $log
        [ParrSetup]::SetupDiDestroyDeviceInfoList($list) | Out-Null
        return
    }

    $bytes = [System.Text.Encoding]::Unicode.GetBytes($hwid + "`0`0")
    if (-not [ParrSetup]::SetupDiSetDeviceRegistryProperty($list, [ref]$data, [ParrSetup]::SPDRP_HARDWAREID, $bytes, $bytes.Length)) {
        "  ${hwid}: SetHardwareID failed $([ComponentModel.Win32Exception]::new([Runtime.InteropServices.Marshal]::GetLastWin32Error()).Message)" | Add-Content $log
        [ParrSetup]::SetupDiDestroyDeviceInfoList($list) | Out-Null
        return
    }
    [ParrSetup]::SetupDiCallClassInstaller([ParrSetup]::DIF_REGISTERDEVICE, $list, [ref]$data) | Out-Null
    [ParrSetup]::SetupDiDestroyDeviceInfoList($list) | Out-Null

    $reboot = $false
    if ([ParrSetup]::UpdateDriverForPlugAndPlayDevices([IntPtr]::Zero, $hwid, $infPath, [ParrSetup]::INSTALLFLAG_FORCE, [ref]$reboot)) {
        "  ${hwid}: установлен в ${instNew} (reboot=$reboot)" | Add-Content $log
    } else {
        "  ${hwid}: UpdateDriver failed $([ComponentModel.Win32Exception]::new([Runtime.InteropServices.Marshal]::GetLastWin32Error()).Message)" | Add-Content $log
    }
}

# Чистка driver store: удаляет ВСЕ опубликованные пакеты виртуального
# драйвера, кроме $keepOem (свежий). Маркер — CatalogFile в oem*.inf.
# /delete-driver без /force отказывается удалять пакет, на котором стоят
# устройства, — текущий пакет защищён и независимо.
function Clear-OldDriverPackages([string]$keepOem) {
    if (-not $keepOem) {
        "  store: свежий oem не определён — чистка пропущена (безопасно)" | Add-Content $log
        return
    }
    $marker = 'VirtualAudioDriver.cat'
    foreach ($f in (Get-ChildItem 'C:\Windows\INF' -Filter 'oem*.inf' -ErrorAction SilentlyContinue)) {
        $head = Get-Content $f.FullName -TotalCount 30 -ErrorAction SilentlyContinue
        if (($head -match [regex]::Escape($marker)).Count -eq 0) { continue }
        if ($f.Name -ieq $keepOem) { continue }
        $out = pnputil /delete-driver "$($f.BaseName).inf" 2>&1
        "  store: удаление $($f.BaseName).inf (exit=$LASTEXITCODE)" | Add-Content $log
        $out | ForEach-Object { "    $_" | Add-Content $log }
    }
}

# ---------- режимы ----------

if ($Uninstall) {
    "--- снятие всех устройств (base + Lane*) ---" | Add-Content $log
    foreach ($root in (Get-ChildItem "HKLM:\SYSTEM\CurrentControlSet\Enum\ROOT" -Recurse -ErrorAction SilentlyContinue)) {
        $p = Get-ItemProperty $root.PSPath -ErrorAction SilentlyContinue
        if ($null -eq $p) { continue }
        $ids = @()
        if ($p.HardwareID -is [array]) { $ids = $p.HardwareID } elseif ($p.HardwareID) { $ids = @($p.HardwareID) }
        foreach ($id in $ids) {
            if ($id -eq 'ROOT\VirtualAudioDriver' -or $id -like 'ROOT\ParrhesiaLane*') {
                $inst = Get-ShortInstanceId $root.Name
                # endpoint-дети первыми (пнпутл не каскадит — призраки)
                foreach ($c in ([ParrSetup]::GetChildren($inst))) {
                    pnputil /remove-device "$c" 2>&1 | Add-Content $log
                    "    child ${c} (exit=$LASTEXITCODE)" | Add-Content $log
                }
                $out = pnputil /remove-device "$inst" 2>&1
                "  ${id}: снятие ${inst} (exit=$LASTEXITCODE)" | Add-Content $log
            }
        }
    }
    "done" | Add-Content $log
    exit 0
}

# --- резолв пакета INF (общий для Add и установки) ---
function Resolve-InfPath {
    $pkg = "$PSScriptRoot\driver-package"
    if (-not (Test-Path "$pkg\VirtualAudioDriver.inf")) {
        $pkg = "$PSScriptRoot\..\driver\x64\Release\package"
    }
    if (-not (Test-Path "$pkg\VirtualAudioDriver.inf")) {
        "ПАКЕТ НЕ НАЙДЕН: ни $PSScriptRoot\driver-package, ни ..\driver\x64\Release\package" | Add-Content $log
        return $null
    }
    return "$pkg\VirtualAudioDriver.inf"
}

if ($Add) {
    if ($Hwid -notmatch '^ROOT\\(VirtualAudioDriver|ParrhesiaLane[1-7])$') {
        "  -Add: недопустимый hwid '$Hwid' (ожидается ROOT\VirtualAudioDriver или ROOT\ParrhesiaLane1..7)" | Add-Content $log
        exit 1
    }
    $inf = Resolve-InfPath
    if (-not $inf) { exit 1 }
    "--- добавление $Hwid ---" | Add-Content $log
    pnputil /add-driver "$inf" 2>&1 | Add-Content $log
    if (Test-DeviceExists $Hwid) {
        "  ${Hwid}: уже установлен — пропуск" | Add-Content $log
        exit 0
    }
    if (-not $Desc) {
        if ($Hwid -eq 'ROOT\VirtualAudioDriver') { $Desc = 'Parrhesia' }
        elseif ($Hwid -match 'ParrhesiaLane(\d)$') { $Desc = "Parrhesia L$($Matches[1])" }
        else { $Desc = 'Parrhesia' }
    }
    Create-Device $Hwid $Desc $inf
    "done" | Add-Content $log
    exit 0
}

if ($Remove) {
    if (-not $Hwid) { "  -Remove: нужен -Hwid" | Add-Content $log; exit 1 }
    "--- удаление $Hwid ---" | Add-Content $log
    Remove-DeviceByHwid $Hwid
    "done" | Add-Content $log
    exit 0
}

# --- полная установка (дефолт) ---
$inf = Resolve-InfPath
if (-not $inf) { exit 1 }

"--- пакет драйвера (pnputil, rebind) ---" | Add-Content $log
$addOut = pnputil /add-driver "$inf" /install 2>&1
$addOut | ForEach-Object { "$_" | Add-Content $log }
# Published Name из вывода (oemNN.inf — токен не локализуется).
$keepOem = ''
$m = [regex]::Matches(($addOut -join "`n"), 'oem\d+\.inf')
if ($m.Count -gt 0) { $keepOem = $m[$m.Count - 1].Value }
"  свежий пакет: $keepOem" | Add-Content $log

"--- devnode'ы ---" | Add-Content $log
$descs = @{
    'ROOT\VirtualAudioDriver' = 'Parrhesia'
    'ROOT\ParrhesiaLane1' = 'Parrhesia L1'
    'ROOT\ParrhesiaLane2' = 'Parrhesia L2'
    'ROOT\ParrhesiaLane3' = 'Parrhesia L3'
    'ROOT\ParrhesiaLane4' = 'Parrhesia L4'
    'ROOT\ParrhesiaLane5' = 'Parrhesia L5'
    'ROOT\ParrhesiaLane6' = 'Parrhesia L6'
    'ROOT\ParrhesiaLane7' = 'Parrhesia L7'
}
foreach ($hw in $hwids) {
    if (Test-DeviceExists $hw) {
        "  ${hw}: уже установлен — пропуск" | Add-Content $log
        continue
    }
    Create-Device $hw $descs[$hw] $inf
}

"--- чистка driver store ---" | Add-Content $log
Clear-OldDriverPackages $keepOem
"done" | Add-Content $log
exit 0
