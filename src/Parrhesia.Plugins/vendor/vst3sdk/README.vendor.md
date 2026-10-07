# VST3 SDK (вендор)

Подмножество [steinbergmedia/vst3sdk](https://github.com/steinbergmedia/vst3sdk)
для хостинга VST3 в Parrhesia. Лицензия — **MIT** (`LICENSE.txt`, сохранена
без изменений; см. также политику товарных знаков VST в разделе
«Trademark and Logo Usage» README апстрима — логотипы не используем).

## Что вендорено

| Каталог | Зачем |
|---|---|
| `pluginterfaces/` | ABI-интерфейсы VST3 (FUnknown, IComponent, IAudioProcessor, IEditController, ...) — нужны и хосту, и тест-плагину |
| `base/` | Базовые утилиты SDK (референсные реализации FUnknown/Strings/...) |
| `public.sdk/` | Hosting/плагиновые хелперы SDK (module factory, hosted classes) |

**Не вендорено**: `vstgui4` (GUI-фреймворк SDK — хосту не нужен, редакторы
рисуются средствами WPF), `cmake`, `doc`, `tutorials`.

## Происхождение

Клон `git clone --depth 1 --filter=blob:none` + `git submodule update --init
pluginterfaces base public.sdk` от (2026-10-07); каталоги скопированы без
`.git`. Обновление: повторить клон и сравнить каталоги вручную (апстрим
редактируется историей — синк по желанию).

## Потребители

- `Vst3Host` (этап A2-A4 линии dev-agNote, см. `docs/parallel-agents.md`);
- нативный тест-VST3-плагин `tests/Parrhesia.Plugins.Native/vst3-test-plugin`.
