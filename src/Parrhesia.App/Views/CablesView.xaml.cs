using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Parrhesia.App.Settings;
using Parrhesia.Audio.Devices;

namespace Parrhesia.App.Views;

/// <summary>
/// Вкладка «Кабели» (В1): список пар In/Out, очередь правок
/// (добавить/удалить/переименовать — оранжевые) и применение одним
/// запросом администратора. Перезагрузка — только если Windows
/// вернула reboot=True (fallback-баннер).
/// </summary>
public partial class CablesView : UserControl
{
    private List<CableActual> _actual = [];
    private bool _rebootHint;

    public CablesView()
    {
        InitializeComponent();
    }

    private static AppSettings Settings => AppServices.Settings;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AppServices.Devices.DevicesChanged += OnDevicesChanged;
        Refresh();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        AppServices.Devices.DevicesChanged -= OnDevicesChanged;
    }

    private void OnDevicesChanged(object? sender, EventArgs e) =>
        Dispatcher.InvokeAsync(Refresh);

    private void OnRefreshClick(object sender, RoutedEventArgs e) => Refresh();

    /// <summary>Факт + сверка очереди + перерисовка списка и баннера.</summary>
    private void Refresh()
    {
        try
        {
            _actual = CableService.GetInstalled();
        }
        catch (Exception ex)
        {
            HeaderText.Text = "Кабели: не удалось прочитать состояние — " + ex.Message;
            return;
        }

        var ops = Settings.CablePendingOps;
        var reconciled = CablePlanner.Reconcile(_actual, ops);
        if (reconciled.Count != ops.Count)
        {
            Settings.CablePendingOps = reconciled;
            Settings.Save();
        }

        CableList.ItemsSource = CablePlanner.BuildRows(_actual, Settings.CablePendingOps);

        var pending = Settings.CablePendingOps.Count;
        HeaderText.Text = pending == 0
            ? $"Кабели: установлено {_actual.Count}/{CablePlanner.MaxCables}"
            : $"Кабели: установлено {_actual.Count}/{CablePlanner.MaxCables} · неприменённых: {pending}";

        if (pending == 0)
        {
            _rebootHint = false;
            Banner.Visibility = Visibility.Collapsed;
            return;
        }

        Banner.Visibility = Visibility.Visible;
        BannerText.Text = _rebootHint
            ? "Перезагрузите ПК для применения изменений"
            : $"Неприменённые изменения к кабелям: {pending}. " +
              "Нажмите «Применить» — потребуется запрос администратора.";
    }

    // ---------- правки (очередь) ----------

    private void OnAddClick(object sender, RoutedEventArgs e)
    {
        var hwid = CablePlanner.NextFreeHwid(
            _actual.Select(a => a.Hwid),
            Settings.CablePendingOps
                .Where(o => o.Op == CableOp.OpAdd)
                .Select(o => o.Hwid));

        if (hwid is null)
        {
            MessageBox.Show(
                Window.GetWindow(this),
                $"Все {CablePlanner.MaxCables} кабелей уже установлены.",
                "Кабели",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var n = hwid.Substring(hwid.Length - 1);
        Settings.CablePendingOps.Add(new CableOp
        {
            Op = CableOp.OpAdd,
            Hwid = hwid,
            Desc = $"Parrhesia L{n}",
        });
        Settings.Save();
        Refresh();
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: CableRow row } || row.Hwid.Length == 0)
        {
            return;
        }

        var ops = Settings.CablePendingOps;
        if (row.State == CableRowState.PendingAdd)
        {
            // Отмена не применённого добавления — очередь чистится сразу.
            ops.RemoveAll(
                o => o.Op == CableOp.OpAdd &&
                     string.Equals(o.Hwid, row.Hwid, StringComparison.OrdinalIgnoreCase));
        }
        else if (!ops.Any(o =>
                     o.Op == CableOp.OpRemove &&
                     string.Equals(o.Hwid, row.Hwid, StringComparison.OrdinalIgnoreCase)))
        {
            ops.Add(new CableOp { Op = CableOp.OpRemove, Hwid = row.Hwid });
        }

        Settings.Save();
        Refresh();
    }

    private void OnRenameInClick(object sender, RoutedEventArgs e) => PromptRename(sender, inSide: true);

    private void OnRenameOutClick(object sender, RoutedEventArgs e) => PromptRename(sender, inSide: false);

    private void PromptRename(object sender, bool inSide)
    {
        if (sender is not FrameworkElement { Tag: CableRow row })
        {
            return;
        }

        var id = inSide ? row.InId : row.OutId;
        var current = inSide ? row.InName : row.OutName;
        if (id.Length == 0)
        {
            return;
        }

        var title = $"Новое имя — {(inSide ? "вход" : "выход")} кабеля L{row.Lane}";
        if (!PromptDialog.Show(Window.GetWindow(this), title, current, out var name) ||
            string.Equals(name, current, StringComparison.Ordinal))
        {
            return;
        }

        var ops = Settings.CablePendingOps;
        ops.RemoveAll(
            o => o.Op == CableOp.OpRename &&
                 string.Equals(o.EndpointId, id, StringComparison.OrdinalIgnoreCase));
        ops.Add(new CableOp { Op = CableOp.OpRename, EndpointId = id, NewName = name });
        Settings.Save();
        Refresh();
    }

    private void OnDiscardClick(object sender, RoutedEventArgs e)
    {
        Settings.CablePendingOps.Clear();
        Settings.Save();
        Refresh();
    }

    // ---------- применение ----------

    private void OnApplyClick(object sender, RoutedEventArgs e)
    {
        var ops = Settings.CablePendingOps;
        if (ops.Count == 0)
        {
            return;
        }

        // Свежий факт: переименование сникшего endpoint'а (кабель удалён
        // в другом месте) неактуально — отбрасываем без попытки.
        var installed = CableService.GetInstalled();
        var installedIds = new HashSet<string>(
            installed.SelectMany(a => new[] { a.InId, a.OutId }),
            StringComparer.OrdinalIgnoreCase);

        var remaining = new List<CableOp>();
        var deviceOps = new List<CableOp>();
        foreach (var op in ops)
        {
            if (op.Op == CableOp.OpRename)
            {
                if (!installedIds.Contains(op.EndpointId))
                {
                    continue;
                }

                // Переименование — non-admin, in-process (EndpointPolicy).
                if (!EndpointPolicy.TryRename(op.EndpointId, op.NewName))
                {
                    remaining.Add(op);
                }
            }
            else
            {
                deviceOps.Add(op);
            }
        }

        var reboot = false;
        if (deviceOps.Count > 0 && !RunElevatedApply(deviceOps, out reboot))
        {
            remaining.AddRange(deviceOps);
            Settings.CablePendingOps = remaining;
            Settings.Save();
            Refresh();
            Banner.Visibility = Visibility.Visible;
            BannerText.Text = "Запрос администратора отменён или не выполнен — изменения не применены.";
            return;
        }

        if (reboot)
        {
            _rebootHint = true;
        }

        Settings.CablePendingOps = remaining;
        Settings.Save();
        Refresh();
        _ = PostApplyRefreshAsync();
    }

    /// <summary>
    /// Один UAC на пакет операций: wrapper-скрипт в %TEMP% (UTF-8 BOM —
    /// обязательен для PS5.1) гоняет install-devices.ps1 поштучно,
    /// копирует его лог после каждого шага (лог перезаписывается) и пишет
    /// результат (коды + reboot). false — UAC отменён или были ошибки.
    /// </summary>
    private static bool RunElevatedApply(List<CableOp> ops, out bool reboot)
    {
        reboot = false;

        var helper = ResolveHelperScript();
        if (helper is null)
        {
            MessageBox.Show(
                "Скрипт инсталлятора (install-devices.ps1) не найден рядом с приложением — " +
                "добавление/удаление кабелей недоступно в этой раскладке.",
                "Кабели",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        var temp = Path.GetTempPath();
        var wrapper = Path.Combine(temp, "parr-cable-apply.ps1");
        var result = Path.Combine(temp, "parr-cable-apply.result");
        File.Delete(result);

        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference = 'Continue'");
        sb.AppendLine("$r = New-Object System.Collections.Generic.List[string]");
        var index = 0;
        foreach (var op in ops)
        {
            index++;
            var flag = op.Op == CableOp.OpAdd ? "'-Add'" : "'-Remove'";
            var logCopy = Path.Combine(temp, $"parr-cable-op{index}.log");
            sb.AppendLine(
                $"$p = Start-Process powershell.exe -Wait -PassThru -NoNewWindow " +
                $"-ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-File','{helper}',{flag},'-Hwid','{op.Hwid}'");
            sb.AppendLine($"$r += '{op.Op} {op.Hwid}=' + $p.ExitCode");
            sb.AppendLine(
                $"Copy-Item \"$env:TEMP\\parrhesia-installer.log\" '{logCopy}' -Force -ErrorAction SilentlyContinue");
        }

        sb.AppendLine($"[IO.File]::WriteAllLines('{result}', $r, [Text.Encoding]::UTF8)");

        // BOM обязателен: PowerShell 5.1 читает бесс BOM как ANSI.
        File.WriteAllText(wrapper, sb.ToString(), new UTF8Encoding(true));

        try
        {
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{wrapper}\"",
                UseShellExecute = true,
                Verb = "runas",
            });
            process?.WaitForExit();
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return false; // UAC отменён пользователем
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "Не удалось запустить применение: " + ex.Message,
                "Кабели",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return false;
        }

        if (!File.Exists(result))
        {
            return false;
        }

        var ok = true;
        foreach (var line in File.ReadAllLines(result, Encoding.UTF8))
        {
            var eq = line.LastIndexOf('=');
            if (eq <= 0 || !int.TryParse(line[(eq + 1)..], out var code))
            {
                continue;
            }

            if (code != 0)
            {
                ok = false;
            }
        }

        // reboot=True в логах шагов → Windows попросила перезагрузку.
        for (var i = 1; i <= index; i++)
        {
            var log = Path.Combine(temp, $"parr-cable-op{i}.log");
            if (File.Exists(log) &&
                File.ReadAllText(log, Encoding.UTF8).Contains("reboot=True", StringComparison.OrdinalIgnoreCase))
            {
                reboot = true;
            }
        }

        return ok;
    }

    /// <summary>Установленная раскладка: {app}\install-devices.ps1; dev — ищем вверх до репозитория.</summary>
    private static string? ResolveHelperScript()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++)
        {
            var direct = Path.Combine(dir, "install-devices.ps1");
            if (File.Exists(direct))
            {
                return direct;
            }

            var repo = Path.Combine(dir, "installer", "install-devices.ps1");
            if (File.Exists(repo))
            {
                return repo;
            }

            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }

        return null;
    }

    /// <summary>
    /// После применения endpoint'ы пересоздаются/переименовываются не мгновенно
    /// (engine стартует по device-change, авто-rename In/Out) — перечитываем
    /// состояние несколько раз с паузами.
    /// </summary>
    private async Task PostApplyRefreshAsync()
    {
        foreach (var delay in new[] { 1500, 3000, 5000 })
        {
            await Task.Delay(delay);
            if (IsLoaded)
            {
                Refresh();
            }
        }
    }
}
