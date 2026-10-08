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

## Установка (Э0, требует участия пользователя)

1. **Один раз за жизнь системы** (права администратора + перезагрузка):
   `bcdedit /set testsigning on`
   - если отказ из-за Secure Boot — отключите Secure Boot в UEFI
     (или включите «test signing» иным способом);
   - проверка: `bcdedit /enum` → Testsigning = Yes.
2. `install.bat` **от администратора** — создаёт root-devnode через devcon
   (пакет берётся из `x64\Release\package`). Перезагрузка НЕ требуется.
3. В «Звуке» появляются **Parrhesia In** и **Parrhesia Out**.
4. Удаление: `uninstall.bat` (админ) — тоже без перезагрузки.

## Сценарий проверки тракта (Э1)

1. Запустить Parrhesia, в инспекторе назначения выбрать
   **«Parrhesia Out (виртуальный)»**, источнику — **«Loopback: Parrhesia In»**.
2. В Windows выбрать **Parrhesia In** как устройство воспроизведения
   (Параметры → Система → Звук или ПКМ по значку динамика).
3. Запустить любое приложение со звуком — он пойдёт в Parrhesia In →
   микшер → Parrhesia Out.
4. Записать «Parrhesia Out» из другого приложения (Audacity/OBS) —
   должен идти микс; в логе (`%AppData%\Parrhesia\logs\app.log`)
   дельта фида без роста (нет сбросов/тишины).

## Статус

- [x] Сборка `.sys` + тестовая подпись пакета (чисто,0 ошибок).
- [x] Имена endpoints: «Parrhesia In» (рендер) / «Parrhesia Out» (захват);
      HW-ID пока `ROOT\VirtualAudioDriver` (смена при разделении инстансов, М2).
- [x] Передача микшера в драйвер: control-устройство `\\.\ParrhesiaFeed`
      (IOCTL_PFEED_WRITE/GET_STATS), кольцевой буфер256 КБ, единственный
      читатель-поток (claim на KSSTATE_RUN), формат-гард (PCM32@48k,
      иначе тишина + лог). Замена тишины в `WriteBytes` чтением из фида.
- [x] User mode: `DriverFeed` + `VirtualSinkPump` (10 мс, дрейф-компенсация),
      ветка `virtual:parrhesia` в `WasapiAudioEngine.StartCore`.
- [ ] Loopback-вход «Parrhesia In» → движок (привязка через UI, эндпоинт
      появится после установки — Э0).
- [ ] Тестовая установка и сквозной прогон (testsigning, гейт пользователя).
- [ ] Динамические инстансы In/Out (М2, спайк) — см. ниже.

## Спайк М2c: динамические инстансы In/Out [shared]

**Как это работает сейчас.** Один root-devnode (`ROOT\MEDIA\0000`,
HW-ID `ROOT\VirtualAudioDriver`) даёт одну пару endpoints (In/Out).
Feed — **глобальный синглтон** в `.sys` (`g_Feed`, `\\.\ParrhesiaFeed`):
все потоки всех клиентов сходятся в один кольцевой буфер. Один KS-поток
захвата (`MAX_INPUT_STREAMS=1`) — в shared-mode это НЕ ограничивает число
приложений (audiodg — единственный клиент KS; каждый app идёт через
общий микшер audiodg), ограничение действует только для exclusive-mode.

**Платформенное ограничение**: KS-фильтры создаёт только PnP — «включить
новый endpoint из процесса» без установки устройства невозможно. Значит
«динамика» = либо заранее поставленные инстансы + переключение
видимости, либо установка/снятие инстансов на лету (root-devnode
ставится/снимается за секунды, без перезагрузки — уже проверено в Э0).

### Варианты

| | Механизм | Динамика | Риск | Оценка |
|---|---|---|---|---|
| **V1** | N HW-ID в INF (`ROOT\ParrhesiaLane0..N`), каждый devnode = своя пара endpoints; feed делается per-adapter (`\\.\ParrhesiaFeed<N>`) | инстансы ставит/снимает приложение (pnputil/devcon, без ребута) | низкий | ~2–3 дня |
| **V2** | V1 + переключение видимости endpoints (IPolicyConfig/SetEndpointVisibility) | мгновенное появление/исчезание в списке устройств, без PnP-шторма | низкий (неофициальный, но стабильный API) | +0.5–1 день |
| **V3** | V1 + установка/удаление инстансов из приложения по требованию | полная динамика | PnP-мельтешение (~2–4 с на цикл) | +0.5 дня поверх V1 |

**Ключевые правки под любой вариант:**
1. `feed.cpp`: `g_Feed` синглтон → по экземпляру на адаптер (имя/индекс
   устройства), IOCTL-устройство своё на инстанс — иначе все lanes
   получают один микшер и разделение бессмысленно.
2. INF: несколько секций `[Manufacturer]` (по HW-ID на lane) с общими
   CopyFiles/Service; имена endpoints пишутся в software key КАЖДОГО
   devnode (механизм MediaCategories из М2a уже per-device ✔);
   различие имён — суффиксы lane либо авто-дедупликация Windows.
3. User mode: `DriverFeed` принимает имя инстанса; движок — мост
   «виртуальный sink #N → feed N» (мультисинки Pc уже позволяют
   несколько sink-планов, нужен маппинг plan→feed).

**HW-ID**: под V1 меняется на линейку `ROOT\ParrhesiaLane<N>` — пункт
README о «смене HW-ID при разделении инстансов» закрывается.

**Рекомендация**: V1+V2 — статические lanes + мгновенная видимость;
«полная динамика» V3 добавляется поверх без переделки.
