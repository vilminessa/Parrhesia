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

## ������ �������������� ����� (2026-10-08) [shared]

- **����� ��������� �������**: A > dev (`62cf8f2`, ���� ������) >
  ����merge-���� dev>dev-agPc (`bc2688b`, ��������� IAudioEngine /
  WasapiAudioEngine �������� ����������-������ �� �������� ���������:
  ���� = �������ink-�������� Pc, ������ � ���������� A: _startStage,
  Restart-�-����/retry, ���-���� �� _sinkIsVirtual) > merge B > dev
  (`b8fbecd`). ����3 (������������ ����): dotnet test exit=0 (���4
  ������, ������� ����� ����� �����), �����0 ����������, driver build exit=0.
- **�������**: �������� dual-push (`git remote set-url --add --push origin
  <github>`) � `git push origin` ��� � ���. GitHub-����� dev ��������
  (GH006: required status check �dotnet test� �� CI ������ Pc) � ������
  push dev � GitHub ��������; dev ����� � Gitea, GitHub-����� �������� ��
  ������� ��������� (����� ���������� / ��������� dev �� PR-flow).
- **����� dev-agNote / dev-agPc** � ���������, � �������� �����
  ������������� ���������.
