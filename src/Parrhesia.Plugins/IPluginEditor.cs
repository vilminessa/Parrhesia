namespace Parrhesia.Plugins;

/// <summary>
/// Опциональный редактор плагина (встроенное окно в контейнер хоста).
/// Протокол: SupportsEditor → Open(hostHwnd) → PreferredSize → Close.
/// Поток: UI-поток (вызовы GUI — main-thread по контрактам CLAP/VST3).
/// </summary>
public interface IPluginEditor
{
    /// <summary>Расширение GUI доступно и формат поддерживается (win32/HWND).</summary>
    bool SupportsEditor { get; }

    /// <summary>
    /// Встраивает окно плагина в дочернее окно хоста (HWND контейнера).
    /// false — отказ (нет расширения, плагин отказал).
    /// </summary>
    bool Open(IntPtr hostWindow);

    /// <summary>Закрывает редактор и освобождает ресуры GUI (идемпотентно).</summary>
    void Close();

    bool IsOpen { get; }

    /// <summary>Желаемый размер контента; измеряется после Open (0×0 — неизвестно).</summary>
    (int Width, int Height) PreferredSize { get; }
}
