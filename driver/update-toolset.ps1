# Добавляет ранний импорт WindowsDriver.Default.props в Toolset.props:
# пороговые версии (TargetPlatformVersion_NI и др.) должны быть определены
# ДО импорта WindowsDriver.OS.props через WindowsDriver.Shared.props.
$ErrorActionPreference = "Stop"

$ts = "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Microsoft\VC\v170\Platforms\x64\PlatformToolsets\WindowsKernelModeDriver10.0"

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

  <!-- Пороговые версии (TargetPlatformVersion_NI/CO/FE/...) — строго ДО Platform.Common.props,
       иначе WindowsDriver.OS.props сравнивает с неопределённым TargetPlatformVersion_NI (MSB4086). -->
  <Import Project="$(WDKContentRoot)build\$(WDKBuildFolder)\WindowsDriver.Default.props"
          Condition="'$(WDKContentRoot)' != '' And Exists('$(WDKContentRoot)build\$(WDKBuildFolder)\WindowsDriver.Default.props')" />

  <Import Project="$(_PlatformFolder)Platform.Common.props" />
</Project>
'@
Set-Content -Path "$ts\Toolset.props" -Value $props -Encoding UTF8
"OK: Toolset.props обновлён (ранний импорт WindowsDriver.Default.props)"
