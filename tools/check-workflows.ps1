# Линт полос beta.yml / release.yml и всех workflows (аналог tools/check_release_yml.py
# из Synfronia - там парность полос держал check_release_yml.py).
#
# Что проверяет:
#   1) Пиннинг: каждое "uses: owner/repo/ref" должно быть запинено на 40-символьный SHA
#      (исключение - slsa-github-generator: запин на тег версии, как в Synfronia).
#   2) Сигнатуры полос: beta - теги b*, формат ^b..., гард origin/dev, --prerelease;
#      release - теги v*, формат ^v..., гард origin/main, без --prerelease.
#   3) Парность: после нормализации lane-специфичных строк (префикс тега, ветка-гард,
#      флаг prerelease, текст notes) beta.yml и release.yml обязаны совпадать байт в байт.
#
# Запуск: ./tools/check-workflows.ps1 (exit 1 при любой ошибке).
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$workflows = Join-Path $root '.github/workflows'
$laneFiles = @(
    (Join-Path $workflows 'beta.yml'),
    (Join-Path $workflows 'release.yml')
)
$allFiles = @((Join-Path $workflows 'ci.yml')) + $laneFiles

$errors = @()

# --- 1. Пиннинг uses: -------------------------------------------------------
foreach ($file in $allFiles) {
    if (-not (Test-Path $file)) {
        $errors += "${name}: нет workflow-файла: $file"
        continue
    }

    $name = Split-Path -Leaf $file
    foreach ($match in [regex]::Matches((Get-Content -Raw -Encoding UTF8 $file), '(?m)^\s*uses:\s*(\S+)\s*$')) {
        $spec = $match.Groups[1].Value
        if ($spec -like './*') {
            continue # локальный reusable-workflow
        }

        if ($spec -match '^slsa-framework/slsa-github-generator/') {
            if ($spec -notmatch '@v\d+\.\d+\.\d+$') {
                $errors += "${name}: SLSA-генератор должен быть запинен на тег версии: $spec"
            }

            continue
        }

        if ($spec -notmatch '@[0-9a-f]{40}$') {
            $errors += "${name}: uses не запинен на SHA (ожидается 40 hex): $spec"
        }
    }
}

# --- 2. Сигнатуры полос -----------------------------------------------------
if ($errors.Count -eq 0 -or (Test-Path $laneFiles[0])) {
    $beta = Get-Content -Raw -Encoding UTF8 $laneFiles[0]
    if ($beta -notmatch 'tags:\s*\[\s*"b\*"\s*\]') { $errors += 'beta.yml: нет триггера tags: ["b*"]' }
    if (-not $beta.Contains('^b[0-9]+(\.[0-9]+){2,3}$')) { $errors += 'beta.yml: нет гарда формата ^b[0-9]+(\.[0-9]+){2,3}$' }
    if (-not $beta.Contains('origin/dev')) { $errors += 'beta.yml: нет гарда «тег из origin/dev»' }
    if (-not $beta.Contains('--prerelease')) { $errors += 'beta.yml: бета-релиз должен создаваться с --prerelease' }
    if (-not $beta.Contains('workflow_dispatch')) { $errors += 'beta.yml: нет workflow_dispatch для пробного прогона' }
}

if ($errors.Count -eq 0 -or (Test-Path $laneFiles[1])) {
    $release = Get-Content -Raw -Encoding UTF8 $laneFiles[1]
    if ($release -notmatch 'tags:\s*\[\s*"v\*"\s*\]') { $errors += 'release.yml: нет триггера tags: ["v*"]' }
    if (-not $release.Contains('^v[0-9]+(\.[0-9]+){2,3}$')) { $errors += 'release.yml: нет гарда формата ^v[0-9]+(\.[0-9]+){2,3}$' }
    if (-not $release.Contains('origin/main')) { $errors += 'release.yml: нет гарда «тег из origin/main»' }
    if ($release.Contains('--prerelease')) { $errors += 'release.yml: стабильный релиз не должен быть --prerelease' }
    if (-not $release.Contains('workflow_dispatch')) { $errors += 'release.yml: нет workflow_dispatch для пробного прогона' }
}

# --- 3. Парность beta.yml <-> release.yml -----------------------------------
function Normalize-Lane([string]$text)
{
    $text = $text -replace '(?m)^\s*#.*$', ''        # YAML-комментарии (структура, не пиннинг)
    $text = $text -replace '(?m)^name: \w+$', 'name: LANE'
    $text = $text -replace '"[bv]\*"', '"TAG*"'
    $text = $text -replace '\^[bv]\[', '^TAG['
    $text = $text -replace '\b[bv]\d+(\.\d+){2,3}\b', 'X.Y.Z.N'
    $text = $text -replace 'Тег [bv] указывает', 'Тег TAG указывает'
    $text = $text -replace 'origin/(dev|main)', 'origin/BRANCH'
    $text = $text -replace '(?i)\b(dev|main)\b', 'BRANCH' # ветка в сообщениях гардов и notes
    $text = $text -replace "TrimStart\('[bv]'\)", "TrimStart('TAG')"
    $text = $text -replace 'notes="[^"]*"', 'notes="NOTES"'
    $text = $text -replace '\s*\\\s*\n\s*--prerelease', '' # флаг только у beta (вместе с пробелом и обратным слэшем)
    return ($text -replace '\r\n', "`n").Trim()
}

if ((Test-Path $laneFiles[0]) -and (Test-Path $laneFiles[1])) {
    $betaNorm = Normalize-Lane (Get-Content -Raw -Encoding UTF8 $laneFiles[0])
    $releaseNorm = Normalize-Lane (Get-Content -Raw -Encoding UTF8 $laneFiles[1])
    if ($betaNorm -ne $releaseNorm) {
        $betaLines = $betaNorm -split "`n"
        $releaseLines = $releaseNorm -split "`n"
        $limit = [Math]::Max($betaLines.Count, $releaseLines.Count)
        for ($i = 0; $i -lt $limit; $i++) {
            $b = if ($i -lt $betaLines.Count) { $betaLines[$i] } else { '<нет строки>' }
            $r = if ($i -lt $releaseLines.Count) { $releaseLines[$i] } else { '<нет строки>' }
            if ($b -ne $r) {
                $errors += "полосы разошлись (строка ${i}:`n  beta:    $b`n  release: $r)"
                break
            }
        }
    }
}

# --- Отчёт ------------------------------------------------------------------
if ($errors.Count -gt 0) {
    foreach ($e in $errors) { Write-Host "::error::$e" }
    exit 1
}

Write-Host 'check-workflows: пиннинг, сигнатуры полос и парность beta/release - ок'
exit 0
