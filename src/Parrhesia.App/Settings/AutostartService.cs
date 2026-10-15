using System.IO;
using Microsoft.Win32;

namespace Parrhesia.App.Settings;

/// <summary>
/// Автозапуск с Windows (U3): запись «Parrhesia» в HKCU…\CurrentVersion\Run
/// = путь к текущему exe. Простой переключатель в настройках.
/// </summary>
internal static class AutostartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Parrhesia";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is string;
        }
        catch
        {
            return false;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        if (key is null)
        {
            return;
        }

        if (enabled)
        {
            var exe = Environment.ProcessPath
                ?? Path.Combine(AppContext.BaseDirectory, "Parrhesia.App.exe");
            key.SetValue(ValueName, $"\"{exe}\"");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
