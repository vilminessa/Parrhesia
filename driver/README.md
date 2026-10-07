# Parrhesia — виртуальный аудио-драйвер (Ф4)

Виртуальный аудио-драйвер для Windows 11 — база для фазы Ф4 проекта Parrhesia
(виртуальные endpoints «вход приложений» → микшер → «виртуальный микрофон»).

## Происхождение и лицензия

Код форкнут с [VirtualDrivers/Virtual-Audio-Driver](https://github.com/VirtualDrivers/Virtual-Audio-Driver)
(в основе — Microsoft Simple Audio Sample / SYSVAD).

- `LICENSE` — MIT (сохранён из апстрима, обязателен при распространении).
- `THIRD_PARTY_NOTICES.md` — уведомления апстрима.
- Адаптация под Parrhesia: `Directory.Build.props`, `Directory.Build.targets`,
  `msbuild.rsp`, `build.bat` (см. ниже) — не изменяют лицензию апстрима.

## Структура

- `Source/Main` — adapter, WaveRT-минипорт, INF (`.inx`), сборка `.sys`.
- `Source/Filters` — topology/wave-фильтры колонок и микрофона.
- `Source/Utilities` — вспомогательные классы (ToneGenerator, savedata).
- `Package` — упаковка драйвера (`.inf` + `.cat` + `.sys`).
- `Directory.Build.props` — **ранняя** инициализация WDK-свойств
  (без неё MSBuild падает: импорт `WindowsDriver.OS.props` идёт раньше
  `WindowsDriver.Default.props`, а DesignTime `WDK.props` с `KM_IncludePath`
  вовсе не подключается — интеграция WDK+VS для Build Tools не выполнена).
- `Directory.Build.targets` — диагностика путей перед компиляцией.
- `msbuild.rsp` — глобальные свойства (пороговые версии, `_NT_TARGET_VERSION_*`,
  `Matching*Present`, `LatestTargetVersion`, отключение InfVerif —
  в этой сборке WDK нет `bin\x86\infverif.dll`).
- `register-toolset.ps1` — **однократно от администратора**: регистрирует тулсет
  `WindowsKernelModeDriver10.0` в MSBuild Build Tools (эквивалент интеграции
  WDK + Visual Studio, которую установщик WDK не делает для Build Tools).
- `update-toolset.ps1` — обновление Toolset.props (ранний импорт
  `WindowsDriver.Default.props`; используется, если свойства всё же
  понадобятся вне rsp).

## Требования

| Компонент | Как поставить |
|---|---|
| VS Build Tools 2022 + workload C++ | `winget install Microsoft.VisualStudio.2022.BuildTools --override "--add Microsoft.VisualStudio.Workload.VCTools --includeRecommended --quiet --wait"` |
| WDK 10.0.26100 | `winget install Microsoft.WindowsWDK.10.0.26100` |
| Тулсет (1 раз, админ) | `powershell -ExecutionPolicy Bypass -File register-toolset.ps1` |

## Сборка

```bat
build.bat
```

Либо вручную:

```bat
msbuild VirtualAudioDriver.sln

(msbuild.rsp подхватывается автоматически — лежит рядом со sln)
```

Выход: `x64\Release\package\` — `VirtualAudioDriver.sys`,
`VirtualAudioDriver.inf`, `virtualaudiodriver.cat`, `package.cer`
(тестовая подпись).

## Установка (требует участия пользователя)

1. Тестовое подключение: `bcdedit /set testsigning on` + **перезагрузка**
   (права администратора).
2. `pnputil /add-driver VirtualAudioDriver.inf /install`
3. В «Звуке» появятся endpoints драйвера.

## Статус

- [x] Сборка `.sys` + тестовая подпись пакета (чисто,0 ошибок).
- [ ] Имена endpoints под Parrhesia («Parrhesia In/Out»).
- [ ] Передача микшера в драйвер (замена ToneGenerator) и loopback-вход.
- [ ] Интеграция с движком Parrhesia (WasapiAudioEngine).
