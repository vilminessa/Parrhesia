# Регистрация тулсета WindowsKernelModeDriver10.0 в MSBuild (Build Tools 2022).
# Аналог интеграции, которую WDK-инсталлятор делает для Visual Studio.
$ErrorActionPreference = "Stop"

$vs = "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Microsoft\VC\v170\Platforms\x64"
$k = "C:\Program Files (x86)\Windows Kits\10\Build\10.0.26100.0"

# 1. Платформенные файлы WDK → ImportAfter платформы
New-Item -ItemType Directory -Force -Path "$vs\ImportAfter" | Out-Null
Copy-Item "$k\x64\ImportAfter\WDK.x64.WindowsKernelModeDriver.Platform.props" "$vs\ImportAfter\" -Force
Copy-Item "$k\x64\ImportAfter\WDK.x64.WindowsDriverCommonToolset.Platform.Targets" "$vs\ImportAfter\" -Force

# 2. Папка тулсета + Toolset.props/targets по образцу v143
$ts = "$vs\PlatformToolsets\WindowsKernelModeDriver10.0"
New-Item -ItemType Directory -Force -Path $ts | Out-Null

$props = @'
<!-- Регистрация тулсета WindowsKernelModeDriver10.0 (эквивалент интеграции WDK + Visual Studio). -->
<Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
  <Import Project="$(MSBuildThisFileDirectory)ImportBefore\*.props" Condition="Exists('$(MSBuildThisFileDirectory)ImportBefore')" />
  <Import Project="$(VCTargetsPath)\Microsoft.Cpp.MSVC.Toolset.x64.props" />
  <Import Project="$(MSBuildThisFileDirectory)ImportAfter\*.props" Condition="Exists('$(MSBuildThisFileDirectory)ImportAfter')" />

  <PropertyGroup>
    <IsKernelModeToolset>true</IsKernelModeToolset>
    <WDKContentRoot Condition="'$(WDKContentRoot)' == ''">C:\Program Files (x86)\Windows Kits\10\</WDKContentRoot>
    <WDKBuildFolder Condition="'$(WDKBuildFolder)' == ''">10.0.26100.0</WDKBuildFolder>
    <WDKBinRoot Condition="'$(WDKBinRoot)' == ''">$(WDKContentRoot)bin\10.0.26100.0\</WDKBinRoot>
  </PropertyGroup>

  <Import Project="$(WDKContentRoot)build\$(WDKBuildFolder)\x64\WindowsKernelModeDriver\WDK.x64.WindowsKernelModeDriver.props"
          Condition="Exists('$(WDKContentRoot)build\$(WDKBuildFolder)\x64\WindowsKernelModeDriver\WDK.x64.WindowsKernelModeDriver.props')" />
  <Import Project="$(_PlatformFolder)Platform.Common.props" />
</Project>
'@
Set-Content -Path "$ts\Toolset.props" -Value $props -Encoding UTF8

$targets = @'
<!-- Регистрация тулсета WindowsKernelModeDriver10.0 (эквивалент интеграции WDK + Visual Studio). -->
<Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
  <Import Project="$(MSBuildThisFileDirectory)ImportBefore\*.targets" Condition="Exists('$(MSBuildThisFileDirectory)ImportBefore')" />
  <Import Project="$(VCTargetsPath)\Microsoft.CppCommon.targets" />
  <Import Project="$(VCTargetsPath)\Microsoft.Cpp.WindowsSDK.targets" />
  <Import Project="$(WDKContentRoot)build\$(WDKBuildFolder)\WindowsDriver.Common.targets"
          Condition="'$(WDKContentRoot)' != '' And Exists('$(WDKContentRoot)build\$(WDKBuildFolder)\WindowsDriver.Common.targets')" />
  <Import Project="$(MSBuildThisFileDirectory)ImportAfter\*.targets" Condition="Exists('$(MSBuildThisFileDirectory)ImportAfter')" />
</Project>
'@
Set-Content -Path "$ts\Toolset.targets" -Value $targets -Encoding UTF8

"OK: тулсет зарегистрирован"
Get-ChildItem $ts | ForEach-Object { "  " + $_.Name }
