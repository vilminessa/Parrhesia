# Параллельная работа двух агентов (ветки dev-agNote / dev-agPc)

> Shared-документ: менять только по согласованию обоих агентов
> (пометка `[shared]` в коммите). Обновлён:2026-10-07, база `ebe1e9f`.

## Ветки

```
dev (заморожен на фазу)
 ├── dev-agNote — Агент Note (линия A): Ф6-V4 VST3-хост + Ф6-V5b окно редактора
 └── dev-agPc   — Агент Pc   (линия B): ресемплинг источников + мониторинг/мультисink
```

Режим: **долгие ветки** — `dev` не двигается до конца фазы; интеграция =
одна стадия в конце (порядок ниже). Внутри ветки: каждая фаза =
зелёный коммит + push (дробление, видимость, бэкап).

## Линия A — `dev-agNote` (VST3 и редакторы)

Фазы:
1. Вендор `vendor/vst3sdk` (MIT; только `pluginterfaces`, `base`,
   `public.sdk` + LICENSE — без VSTGUI) + нативный тест-VST3-плагин
   (пример Synth/Sine из SDK, сборка скриптом).
2. Spike (≤1 сутки): P/Invoke по COM-вtable против C-шима на hosting-классах;
   проверить новый VST3 C API (3.8). Зафиксировать выбор.
3. `Vst3Host` (load/process/state/latency, зеркало CLAP-тестам) +
   `SlotChainManager`: фабрика `slot.Format → Clap | Vst3`.
4. Пикер: VST3 в списке и выбор.
5. Ф6-V5b: окно редактора — CLAP gui (win32 HWND), затем VST3 IPlugView.

DoD: тест-плагин VST3 проходит тот же набор, что CLAP (load/process/state/
latency); оба формата грузятся из пикера; окно редактора открывается;
`dotnet test` exit=0; `GraphSerializer`/Core не тронуты.

## Линия B — `dev-agPc` (движок: ресемплинг + мониторинг)

Фазы:
1. Ресемплинг: сейчас источник с несовпадающим форматом (BT44.1k при
   движке48k) ПРОПУСКАЕТСЯ — вставить линейный ресемплер между
   `DataAvailable` и кольцом (кольцо остаётся в формате движка),
   снять скип-запрет в `OpenSources`; тесты (частоты/длина/тишина).
2. Мониторинг: sink=виртуал → параллельный `WasapiPlayer` на выбранное
   реальное устройство (каждый player вызывает `ProcessBlock(sinkId)`;
   GraphProcessor не меняется); хранение monitor-устройства — `AppSettings`.
3. Мультисink-основа: StartCore на список sink'ов, статус обоих.
4. Микшер: комбобокс монитора + индикация.

DoD:44.1k-источник при движке48k не пропускается (тест до/после); монитор
вкл/выкл не роняет тракт (тесты + смоук); `dotnet test` exit=0;
`GraphSerializer`/Core не тронуты.

## Карта владения файлами (антиконфликт)

**A (dev-agNote) владеет:**
```
src/Parrhesia.Plugins/**                 tests/Parrhesia.Plugins.Tests/**
vendor/vst3sdk/** + нативный VST3-тест-плагин
src/Parrhesia.App/Views/PluginPickerWindow.cs
src/Parrhesia.App/Views/GraphView.xaml(.cs)
src/Parrhesia.Audio/Processing/SlotChainManager.cs
tests/Parrhesia.Audio.Tests/Processing/**
```

**B (dev-agPc) владеет:**
```
src/Parrhesia.Audio/Engine/**
src/Parrhesia.App/Views/Mixer/**
src/Parrhesia.App/Settings/**
tests/Parrhesia.Audio.Tests/Engine*|Buffers*
```

**Shared — минимальный diff, пометка `[shared]` в коммите, пиног второму
агенту до/после коммита:**
```
Parrhesia.slnx, *.csproj, .gitignore, debug.bat, корневой README,
src/Parrhesia.App/AppServices.cs, src/Parrhesia.Core/**, этот файл
```
Правило: правит тот, кому нужно первому; второй при конфликте уступает
и переносит своё поверх.

## Контракты

- Эволюция `IAudioPlugin` / `IAudioEngine` / `DeviceSpec` — additive-only
  (новые члены с дефолтами); изменение сигнатур — пинг второму агенту.
- Версия `GraphSerializer` за фазу меняется не более чем одной веткой
  (сейчас — никем).
- Каждый коммит зелёный для своей области: `dotnet test` exit=0
  (+ `driver\build.bat` при правках driver/).

## Синк и интеграция

1. Внутри фазы: коммиты + push в свою ветку; синк `git merge origin/dev`
   — только если dev сдвинулся (hotfix), конфликты решает владелец ветки.
2. Финал фазы A: отдельная сессия-интегратор делает
   `git checkout dev && git merge --no-ff dev-agNote` → полный гейт
   (`dotnet test` + смоук-запуск приложения + `driver\build.bat`) → push.
3. `git checkout dev-agPc && git merge origin/dev` (в ветке B!) → конфликты
   решает B → гейт → push.
4. `git checkout dev && git merge --no-ff dev-agPc` → финальный гейт → push.
5. Удалить ветки.

## Ритуалы

- Коммиты: `type(scope): ...` + тело; `[shared]`-правки помечать явно.
- Отчёт в конце сессии: что готово, что тронуто, что blocked.
- Ветка живёт ≤5 дней до первой интеграции (дробить фазу, не тянуть).

## Журнал отклонений от карты владения

- **dev-agNote, A3** (`8329950`): аддитивно добавлено в зону B —
  `IAudioEngine.GetSlotInstance(nodeId, slotIndex)` + реализация в
  `WasapiAudioEngine.cs` (доступ к живому экземпляру слота для окна
  редактора; оба изменения — один метод, без изменения существующих
  сигнатур). Агент Pc: при конфликте в этих строках уступает/переносит
  своё поверх — они аддитивны и тривиальны.

## Старт

```bat
git checkout dev-agNote   :: Агент Note — фаза A1 (вендор SDK + тест-плагин)
git checkout dev-agPc     :: Агент Pc — фаза B1 (ресемплинг)
```

## Статус интеграционной волны (2026-10-08) [shared]

- **Волна выполнена целиком**: A > dev (`62cf8f2`, гейт зелёный) >
  предmerge-синк dev>dev-agPc (`bc2688b`, конфликты IAudioEngine /
  WasapiAudioEngine разрешил интегратор-сессии по указанию владельца:
  база = мультисink-рефактор Pc, поверх — надстройки A: _startStage,
  Restart-в-фоне/retry, эхо-гард на _sinkIsVirtual) > merge B > dev
  (`b8fbecd`). Гейт3 (объединённая база): dotnet test exit=0 (все4
  сборки, включая тесты обеих линий), смоук0 исключений, driver build exit=0.
- **Зеркала**: настроен dual-push (`git remote set-url --add --push origin
  <github>`) — `git push origin` шлёт в оба. GitHub-ветка dev защищена
  (GH006: required status check «dotnet test» от CI агента Pc) — прямой
  push dev в GitHub отклонён; dev залит в Gitea, GitHub-догон отложено до
  решения владельца (снять требование / перевести dev на PR-flow).
- **Ветки dev-agNote / dev-agPc** — исчерпаны, к удалению после
  подтверждения владельца.

## Репозиторий и зеркала (2026-10-08) [shared]

- **Первичный remote — GitHub** (`origin = https://github.com/vilminessa/Parrhesia`):
  все push'и идут только сюда (`git push origin`). Forgejo
  (`vilmpc:3000`) — локальное зеркало, remote `forgejo` только для чтения;
  pull-зеркало Forgejo>себя настраивает владелец в веб-интерфейсе Forgejo.
- **Защита GitHub**: legacy-защита (required status check «dotnet test»)
  снята — она блокировала прямой push (GH006) при процессе волн. Активен
  ruleset `minimal-protect` на `dev`/`main`: только запрет удаления веток и
  force-push, без status checks. Default-ветка — `main` (обновлён до `dev`,
  fast-forward).
- **CI** (`.github/workflows/ci.yml`): каждый push в `dev`/`main` и PR —
  сборка трёх нативных артефактов батами (CLAP-тест-плагин,
  VST3-тест-плагин, `parr_vst3_shim`) + полный `dotnet test`. Локальные
  гейты волны (dotnet test + смоук + driver/build.bat) остаются источником
  правды; CI — подтверждение. Линт полос: `tools/check-workflows.ps1`
  (запуск через `powershell -ExecutionPolicy Bypass -File ...`).
- **Дефекты CI, выправленные при интеграции**: `user32.lib` для GUI-функций
  нативного CLAP-плагина; в workflow не собирались VST3-тест-плагин и шим
  (тесты не находили `test-plugin-vst3.dll`); двоеточие в `name:` шага
  ломало YAML (раны умирали с 0 jobs). В bat'никах добавлен vswhere-fallback
  (нет BuildTools 2022 — например, на GitHub-runner).
- **Токены**: в репозиторий не попадают; GitHub PAT хранится в Windows
  Credential Manager. Рекомендуется ротация токенов, выданных в чате.
- Ветки `dev-agNote` / `dev-agPc` — не удаляются (решение владельца).

## Статус интеграционной волны2 (2026-10-08) [shared]

- **Волна завершена**: `feat/agNote-params` > dev (`1f7462a`) > синк dev >
  `feat/agNote-m2` (`daabbed`, shим авто-слился: mutex+params) > merge m2 >
  dev (`b584986`). Конфликтов нет ни на одном шаге.
- **Гейты**:256 тестов (Core94/Plugins44/Audio115/App2) зелёные на всех
  трёх шагах; смоук0 исключений; `driver\build.bat` exit=0.
  ВНИМАНИЕ: после смены ветки нативные DLL (test-plugin/shim) пересобирать
  тремя bat — иначе тесты падают на устаревших бинарях.
- **Содержание волны**: параметры плагинов (CLAP+VST3+UI+персистентность),
  М2-ланы (per-adapter feed,4 HW-ID, авто-имена In1..Out4, эхо-guard по
  InstanceId), инсталлятор Ф5, диагностика драйвера, фиксы CI.
- **Ветки `dev-agNote`/`dev-agPc`** — не удаляются (решение владельца);
  `feat/agNote-params`/`feat/agNote-m2` — исчерпаны, к удалению после
  подтверждения владельца.

## Чистка веток (2026-10-09) [shared]

- **Репо приведено к схеме «только main + dev»** (распоряжение владельца;
  ранний запрет на удаление dev-agNote/dev-agPc снят):
  - перед чисткой бекап: `D:\Parrhesia-backups\Parrhesia-full-20261009.bundle`
    (все ветки+теги, верифицирован) + копия рабочего дерева;
  - локально удалены: dev-agNote, dev-agPc, feat/agNote-params,
    feat/agNote-m2 (все были полностью слиты в dev — проверено);
  - GitHub: те же4 ветки удалены (dev-agPc была под классической
    защитой — защита снята перед удалением);
  - main: обновлён fast-forward до dev, локальная ветка main создана;
    тег b0.1.0 сохранён.
- **Forgejo = pull-зеркало (read-only)**: запись запрещена (403) —
  ветки чистятся синком зеркала. НЕДЕЙСТВИТЕЛЬНО: зеркало не обновляет
  ref'ы (original_url корректен, mirror_updated двигается при
  mirror-sync POST, но fetch не доходит — dev застрял на c7c5bd3,
  удалённые ветки остаются). Нужна проверка владельца: веб-морда
  Forgejo → репозиторий → Мирроринг → статус/лог последнего синка.
- **Кодировка**: секции этого файла, дописанные PowerShell Add-Content,
  были в cp1251 (мохой на GitHub) — файл перестроен в UTF-8 целиком.
- **Правило (ПОВОД: инцидент с `2.profile.json`)**: в PowerShell5.1
  ВСЕГДА `Get-Content -Encoding UTF8` и `Set-Content -Encoding UTF8`
  (без флага PS читает/пишет ANSI-кодировку и портит кириллицу);
  файлы-тексты править инструментами редактирования, не `Add-Content`.
  Выявлено и вычищено: `2.profile.json` (4 имени узлов), `docs/` (целиком),
  `install-devices.ps1` (BOM),4 build-батов (`??????` в echo/rem).

## [shared] Волна В1 — кабели In/Out (2026-10-09, `0e1f9c2`+`0d289bb`)

**Решение владельца**: кабельная семантика на уровне драйвера — «звук вошёл
в Parrhesia InN → выходит из OutN» без участия приложения и без loopback
(списки устройств не засоряются). Loopback/virtual-sink остаются только
как «продвинутый» путь: пока приложение держит хэндл фида — кабель уступает.

- **Драйвер** (`feed.cpp`/`minwavertstream.cpp`): `WriteCable` (конверт
  16/24/32→PCM32 в кольцо), `PushRenderToFeed` из `UpdatePosition`,
  счётчик user mode-хэндлов (CREATE/CLOSE), метки `T_CblOk`/`T_CblFmtBad`
  в сервис-ключ. Фикс: `GetOwnFeed` не должен запрещать render-поток.
  Статистика `PFEED_STATS` +`LoopBytes`/`Writers` (C#-зеркало синхронно!).
- **INF**: HW-ID до `ROOT\ParrhesiaLane7` (предел `PFEED_MAX_INSTANCES=8`).
- **Живые операции (спайк подтвердил: ребут НЕ нужен)**: remove — pnputil
  `/remove-device` с КОРОТКИМ instance-id (`ROOT\MEDIA\000N`); create —
  SetupAPI с ПОЛНЫМ instance-id + `CreationFlags=0`; `-Add`/`-Remove`
  в `install-devices.ps1`. Дети (endpoints) снимать ПЕРЕД девноутом —
  `pnputil` не каскадит (иначе призраки state=1); чистка driver store
  после rebind (было13 устаревших oem*.inf).
- **3 бага Ф5 в install-devices** (маскированы идемпотентностью, что
  «идемпотентность проверена» = никогда не выполнялось): длинный instance-id
  в pnputil; `DICD_GENERATE_ID` с hwid → `SPAPI_E_INVALID_DEVINST_NAME`;
  `CharSet` без Unicode в `SetupDiSetDeviceRegistryProperty` → порча MULTI_SZ
  (`HardwareID` из19 строк) → `NO_SUCH_DEVINST`.
- **Вкладка «Кабели»**: очередь правок (add/remove/rename) в
  `settings.json` → оранжевые строки/баннер → «Применить» = один UAC
  (wrapper в `%TEMP%`, **UTF-8 BOM обязателен**) или «Отменить»;
  «Перезагрузите ПК…» — только при `reboot=True`. Сверка `Reconcile`
  самоочищает применённое (переживает перезагрузку).
- **ДИАГНОСТИКА И РИСКИ (помнить!)**:
  - **каждый rebind/пакетный апдейт драйвера ПЕРЕСОЗДАЁТ все endpoint GUID**
    (было3 генерации за сессию) → сырые id в профиле ломаются →
    БЭКЛОГ: хранить имя устройства в профиле + авто-rebind по имени;
  - движок НЕ ретраит неудачный старт сника («ни одно назначение не
    открылось» залипает до device-change) → БЭКЛОГ: авто-повтор;
  - транспорт команд съедает пробел перед цифрами (`-Last N`, `-Seconds N`,
    `$buf N`): писать через срезы массивов и `[Threading.Thread]::Sleep(N)`;
  - helper-функции PowerShell, печатающие лог И возвращающие значение,
    ломают присваивание (в pipeline уходят обе строки) — не использовать
    в виде `$x = Func ...`.

## [shared] Волна E — чистый и быстрый тракт (2026-10-09, `b96e845`)

Повод: владелец — «звук очень ломаный, должен быть идеально точный и без
задержки» (схема Микрофон→In1→Out1→Динамики).

- **Корень «ломаного»**: `GraphProcessor.ProcessBlock` при каждом пуле
  ЛЮБОГО сника дренажил ВСЕ кольца источников → при2+ выходах второй
  пулл (общий `_renderGate`) видел пустоту: «под» +960000/5с = ровно
  2 источника×2к×48к. **Фикс: эпохальное микширование** (сетка2мс <
  мин. WASAPI-периода2.67мс): узел =1 расчёт/эпоху, пулы идут в кэш.
  Семантика для тестов: `Invalidate()` = «новый аудио-цикл» (хелперы
  тестов зовут его перед блоком — в рантайме НЕ вызывать).
- **Задержка**: `WithLowLatency(true)` (IAudioClient3) + фолбэки
  (render20мс/capture30мс вместо50/100) + MMCSS «Pro Audio» + убран
  `WithFormat` (NAudio3 отказывает от low-latency при чужом формате —
  mix format и есть float32/48к). ФАКТ: источники10мс, выходы10/10мс.
  Итог тракта ≈50-60мс (было ~250-350).
- **E3 — стабильность профилей**: `DeviceNameCache` (id→имя,
  `%APPDATA%\Parrhesia\device-names.json`); при `E_NOTFOUND` — поиск
  ЕДИНСТВЕННОГО совпадения по имени → `SetNodeDevice`+автосейв (лог
  «Привязка восстановлена по имени»). `ApplyVirtualEndpointNames`
  перенесён в САМОЕ НАЧАЛО старта — иначе rename после сников недостижим
  при их отказе (замкнутый круг). Порядок спасённого: preflight-прогон
  для НАПОЛЕНИЯ кэша → перебинд → старт.
- **Драйвер**: монотонный push (после STOP-обнуления позиции — пересинк
  без записи, дубль-сегмент DMA убран), только `KSSTATE_RUN`, водяной
  знак кабеля ~85мс (`CableDropped` в PFEED_STATS — C#-зеркало ОБЯЗАТЕЛЬНО).
- **Замеры после**: xrun-дельт **0** (стабильно), LoopΔ=DelivΔ=номинал,
  Dropped=CableDrop=Underrun=0,0 ошибок/варнингов. Гейт:273 теста.
- **Открытый вопрос**: loopback-источники + low-latency = конфликт NAudio
  («no loopback» в требованиях IAudioClient3) — путь уходит в standard
  fallback (30мс) — проверить при следующем loopback-тесте.

## [shared] Волна M — поэтапные метрики задержки (2026-10-09, `ff7ffba`)

- **Метрики**: `LatencyReport.Build` (пороги ok≤70/warn≤120/bad>120мс;
  этап>50 → не ниже warn; серьёзность = ДЕЛЬТА потерь кабеля к базе первого
  отчёта — стартовый прогрев не «потеря»). Источники: клиенты NAudio
  (`LatencyMilliseconds`), кольца (`Available`→мс), кабель (`LevelBytes`
  из PFEED_STATS →мс), микшер (`MixerStats`: CPU мкс/бл, бл/с, кадр).
  Сводка в лог каждые5с; панель «Задержка по этапам» во вкладке
  «Диагностика» (итог цветом, плашка проблем).
- **Диагностика фида non-intrusive**: `Writers` в драйвере считается только
  по write-хэндлам (`DesiredAccess & FILE_WRITE_DATA` в CREATE, декремент на
  CLEANUP по FileObject-слотам) — `TryReadStatsShared` (GENERIC_READ)
  НЕ замирает кабель.
- **Фикс семантики кабеля**: тишина-догон в `Read` не обгоняет фронт записи
  (`read = min(read+len, write)`) — иначе уровень уходил в минус →
  watermark видел ULONGLONG-underflow → дропал ВЕСЬ вход (симптом
  «задержка+ломаный ещё сильней»). `LevelBytes` в GetStats тоже клампится.
- **Откат эпохального кэша**: `ProcessBlock` снова свеж на каждый пулл,
  читает только upstream-ветку сника (все рёбра, включая disabled — метры
  живут). Disjoint-схема = каждый вход читается своим потребителем.
  Shared-входы (общий источник на2 сника) — двойной дренаж остаётся,
  отдельный латч — задача.
- **⚠️ ЭКСПЛУАТАЦИЯ ДРАЙВЕРА (важно!)**: образ .sys в памяти выгружается
  только когда сняты ВСЕ девноуты. Обновление пакета/пересоздание ОДНОГО
  устройства переиспользует СТАРЫЙ модуль (симптом: «фикс есть в файле, а
  в ядре старое» — проверяется по IOCTL ret/поведению). Решение: полный
  цикл `install-devices.ps1 -Uninstall` → установка (без ребута, ~10с).
  `UpdateDriverForPlugAndPlayDevices` может вернуть `reboot=True` — это
  штатный фолбэк UX (баннер «Перезагрузите ПК»).
- **pnputil-капризы**: `/restart-device` и `/disable-device` для базового
  `ROOT\MEDIA\0000` возвращают exit50 — используем remove+add-цикл.
- **Замеры после**: кабель Level≈11-13мс стабилен, LoopΔ=DelivΔ=номинал,
  CableDrop Δ=0, отчёт «итого≈71мс [warn]»,0 xrun-дельт. Гейт:279 тестов.