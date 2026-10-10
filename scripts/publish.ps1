# Publishes Parrhesia as a runnable folder (Release, framework-dependent by default).
# Usage:
#   ./scripts/publish.ps1 -Version 0.9.0              # folder + zip
#   ./scripts/publish.ps1 -Version 0.9.0 -SelfContained
param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [switch]$SelfContained,

    [string]$Output = "artifacts/publish"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "src\Parrhesia.App\Parrhesia.App.csproj"
$out = Join-Path $root $Output

if (Test-Path $out) {
    Remove-Item $out -Recurse -Force
}

$publishArgs = @(
    "publish", $project,
    "-c", "Release",
    "-p:Version=$Version",
    "--output", $out,
    "--nologo"
)
if ($SelfContained) {
    $publishArgs += @("--self-contained", "true")
} else {
    $publishArgs += @("--self-contained", "false")
}

& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed ($LASTEXITCODE)"
}

$exe = Join-Path $out "Parrhesia.App.exe"
if (-not (Test-Path $exe)) {
    throw "publish output has no Parrhesia.App.exe"
}

$shim = Join-Path $out "parr_vst3_shim.dll"
if (-not (Test-Path $shim)) {
    Write-Warning "parr_vst3_shim.dll is MISSING - VST3 plugins will not load. Build it first: src\Parrhesia.Plugins\native\vst3-shim\build-vst3-shim.bat"
}

$zip = Join-Path $root "artifacts\Parrhesia-$Version.zip"
if (Test-Path $zip) {
    Remove-Item $zip -Force
}
Compress-Archive -Path (Join-Path $out "*") -DestinationPath $zip

Write-Host "OK: $out"
Write-Host "OK: $zip"
Write-Host "Run: $exe (requires .NET10 Desktop Runtime unless -SelfContained)"
