using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Parrhesia.App.Controls;
using Parrhesia.App.Rendering;

namespace Parrhesia.App.Views;

/// <summary>
/// Стресс-тест отрисовки: 100 LED-метров на общем тике + метрики FPS/кадр/CPU.
/// Работает, пока вкладка видима; кнопка выключает нагрузку для замера в покое.
/// </summary>
public partial class DiagnosticsView : UserControl
{
    private const int MeterCount = 100;
    private const int StatsIntervalSeconds = 1;

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
        if (!_stoppedByUser)
        {
            Start();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        Stop();
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

        _frames = 0;
        _windowStart = now;
        _lastCpuTime = _process.TotalProcessorTime;
        _lastCpuWall = now;
    }
}
