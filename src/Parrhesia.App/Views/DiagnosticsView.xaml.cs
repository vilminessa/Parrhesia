using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Parrhesia.App.Controls;
using Parrhesia.App.Rendering;
using Parrhesia.Audio.Engine;
using Parrhesia.Core.Graph;

namespace Parrhesia.App.Views;

/// <summary>
/// Стресс-тест отрисовки: 100 LED-метров на общем тике + метрики FPS/кадр/CPU.
/// Работает, пока вкладка видима; кнопка выключает нагрузку для замера в покое.
/// Кнопка «Сохранить отчёт…» (U2) собирает снимок движка/задержек/узлов-плагинов/лога.
/// </summary>
public partial class DiagnosticsView : UserControl
{
    private const int MeterCount = 100;
    private const int StatsIntervalSeconds = 1;

    /// <summary>Кольцевой буфер лога движка для отчёта (U2); дисплей его не использует.</summary>
    private const int LogCapacity = 200;

    private readonly Queue<string> _logBuffer = new();

    private readonly List<LedMeterControl> _meters = [];
    private readonly float[] _amplitude = new float[MeterCount];
    private readonly float[] _frequency = new float[MeterCount];
    private readonly float[] _phase = new float[MeterCount];
    private readonly Random _random = new(7);

    private Process? _process;
    private TimeSpan _lastCpuTime;
    private double _lastCpuWall;

    private bool _running;
    private bool _stoppedByUser;
    private int _frames;
    private double _windowStart;

    public DiagnosticsView()
    {
        InitializeComponent();

        for (var i = 0; i < MeterCount; i++)
        {
            var meter = new LedMeterControl
            {
                Margin = new Thickness(3, 2, 3, 2),
                Width = 20,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            _meters.Add(meter);
            Meters.Children.Add(meter);

            _amplitude[i] = 0.35f + 0.65f * (float)_random.NextDouble();
            _frequency[i] = 0.4f + 2.6f * (float)_random.NextDouble();
            _phase[i] = (float)(_random.NextDouble() * Math.PI * 2);
        }

        MetersText.Text = MeterCount.ToString();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AppServices.Engine.Log += OnEngineLog;
        if (!_stoppedByUser)
        {
            Start();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        AppServices.Engine.Log -= OnEngineLog;
        Stop();
    }

    private void OnEngineLog(object? sender, EngineLogEntry entry)
    {
        lock (_logBuffer)
        {
            _logBuffer.Enqueue($"[{entry.Timestamp:HH:mm:ss}] {entry.Level}: {entry.Message}");
            while (_logBuffer.Count > LogCapacity)
            {
                _logBuffer.Dequeue();
            }
        }
    }

    /// <summary>Отчёт диагностики (U2) в файл — для багрепортов и поддержки.</summary>
    private void OnSaveReportClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Отчёт диагностики",
            Filter = "Текстовый отчёт (*.txt)|*.txt",
            FileName = $"parrhesia-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }

        try
        {
            File.WriteAllText(dialog.FileName, BuildReport());
            Toast.Show($"Отчёт сохранён: {Path.GetFileName(dialog.FileName)}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                Window.GetWindow(this), ex.Message, "Не удалось сохранить отчёт",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private string BuildReport()
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Parrhesia — отчёт диагностики: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine(
            "Версия: " +
            (Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?"));

        var status = AppServices.Engine.Status;
        builder.AppendLine();
        builder.AppendLine("== Движок ==");
        builder.AppendLine(
            $"работует={status.IsRunning} · частота={status.SampleRate} Гц · каналы={status.Channels} · " +
            $"квант={status.OutputPeriodMs} мс");
        builder.AppendLine(
            $"выходы: [{string.Join(", ", status.SinkNames)}] · основной: {status.SinkName ?? "-"}");
        builder.AppendLine(
            $"xrun под/переп: {status.UnderrunSamples}/{status.OverflowSamples} · " +
            $"монитор: {status.MonitorName ?? "выкл"}{(status.MonitorActive ? " (играет)" : string.Empty)}");
        builder.AppendLine(
            "спящие источники («звучат в никуда»): " +
            (status.SleepingSourceNodes.Count == 0
                ? "нет"
                : string.Join(", ", status.SleepingSourceNodes.Select(
                    id => AppServices.Graph.FindNode(id)?.Name ?? id.ToString("N")))));

        var report = AppServices.Engine.GetLatencyReport();
        builder.AppendLine();
        builder.AppendLine($"== Задержка: итого ≈{report.TotalMs:0.#} мс [{report.Level}] ==");
        foreach (var stage in report.Stages)
        {
            builder.AppendLine($"  {stage.Name}: {stage.Ms:0.#} мс — {stage.Detail}");
        }

        foreach (var issue in report.Issues)
        {
            builder.AppendLine($"  ! {issue}");
        }

        builder.AppendLine();
        builder.AppendLine("== Узлы-плагины ==");
        var plugins = AppServices.Graph.Nodes.Where(n => n.Kind == NodeKind.Plugin).ToList();
        if (plugins.Count == 0)
        {
            builder.AppendLine("  нет");
        }

        foreach (var node in plugins)
        {
            var plugin = AppServices.Engine.GetPluginStatus(node.Id);
            builder.AppendLine(plugin is null
                ? $"  «{node.Name}»: слоты не смоделированы"
                : $"  «{node.Name}»: загружен={plugin.Loaded} жив={plugin.Alive} pid={plugin.ProcessId} " +
                  $"попыток={plugin.SpawnAttempts} ретрай через {plugin.SecondsToRetry} с " +
                  $"пропуски={plugin.Drops} блоков={plugin.ProcessedBlocks}" +
                  (plugin.Errors.Count > 0 ? $"\n      ошибка: {plugin.Errors[0]}" : string.Empty));
        }

        builder.AppendLine();
        builder.AppendLine("== Лог движка ==");
        lock (_logBuffer)
        {
            foreach (var line in _logBuffer)
            {
                builder.AppendLine("  " + line);
            }
        }

        return builder.ToString();
    }

    private void OnStressClick(object sender, RoutedEventArgs e)
    {
        if (_running)
        {
            _stoppedByUser = true;
            Stop();
        }
        else
        {
            _stoppedByUser = false;
            Start();
        }
    }

    private void Start()
    {
        if (_running)
        {
            return;
        }

        _running = true;
        _frames = 0;
        _windowStart = 0;
        _process = Process.GetCurrentProcess();
        _lastCpuTime = _process.TotalProcessorTime;
        _lastCpuWall = 0;
        RenderTicker.Subscribe(OnTick);
        StressButton.Content = "Стресс: вкл";
    }

    private void Stop()
    {
        if (!_running)
        {
            return;
        }

        _running = false;
        RenderTicker.Unsubscribe(OnTick);
        StressButton.Content = "Стресс: выкл";
    }

    private void OnTick(double now, double dt)
    {
        // Синтетический сигнал: дыхание (две частоты) + редкие всплески.
        for (var i = 0; i < _meters.Count; i++)
        {
            var envelope = 0.5f + 0.5f * MathF.Sin((float)now * _frequency[i] + _phase[i]);
            var value = envelope * _amplitude[i];
            if (_random.NextDouble() < 0.01)
            {
                value = (float)_random.NextDouble();
            }

            var meter = _meters[i];
            meter.Value = value;
            meter.Peak = MathF.Max(value, meter.Peak - (float)dt * 1.2f);
        }

        if (_windowStart <= 0)
        {
            _windowStart = now;
            _lastCpuWall = now;
            return;
        }

        _frames++;
        var elapsed = now - _windowStart;
        if (elapsed < StatsIntervalSeconds)
        {
            return;
        }

        var fps = _frames / elapsed;
        FpsText.Text = fps.ToString("0.0");
        FrameText.Text = (1000.0 / fps).ToString("0.0") + " мс";

        var cpuUsed = _process!.TotalProcessorTime - _lastCpuTime;
        var wall = (now - _lastCpuWall) * 1000.0;
        var cpuPercent = 100.0 * cpuUsed.TotalMilliseconds / wall / Environment.ProcessorCount;
        CpuText.Text = cpuPercent.ToString("0") + " %";

        RefreshLatency();

        _frames = 0;
        _windowStart = now;
        _lastCpuTime = _process.TotalProcessorTime;
        _lastCpuWall = now;
    }

    /// <summary>Поэтапная задержка тракта: список, итог, цвет, проблемы (M-волна).</summary>
    private void RefreshLatency()
    {
        try
        {
            var report = AppServices.Engine.GetLatencyReport();
            LatencyList.ItemsSource = report.Stages;
            LatencyTotalText.Text = $"итого ≈{report.TotalMs:0} мс";

            var brush = report.Level switch
            {
                LatencyLevel.Ok => LevelBrush("Brush.Success"),
                LatencyLevel.Warn => LevelBrush("Brush.Accent"),
                _ => LevelBrush("Brush.Danger"),
            };

            LatencyTotalText.Foreground = brush;
            if (report.Issues.Count == 0)
            {
                LatencyIssuesText.Visibility = Visibility.Collapsed;
            }
            else
            {
                LatencyIssuesText.Visibility = Visibility.Visible;
                LatencyIssuesText.Text = "⚠ " + string.Join(" · ", report.Issues);
                LatencyIssuesText.Foreground = LevelBrush("Brush.Danger");
            }
        }
        catch (Exception)
        {
            // Диагностика не должна ронять тик отрисовки.
        }
    }

    private System.Windows.Media.Brush LevelBrush(string key) =>
        TryFindResource(key) as System.Windows.Media.Brush
            ?? System.Windows.Media.Brushes.White;
}
