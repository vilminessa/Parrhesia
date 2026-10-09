using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Parrhesia.App.Controls;
using Parrhesia.App.Rendering;
using Parrhesia.App.Views.Effects;
using Parrhesia.App.Views.Mixer;
using Parrhesia.Audio.Devices;
using Parrhesia.Audio.Engine;
using Parrhesia.Core.Graph;

namespace Parrhesia.App.Views;

/// <summary>
/// Вкладка «Микшер»: пульты каналов (источники → шины → назначения) с
/// фейдерами, метрами и кнопками M/S/B, разложенные по зонам (авто) и
/// ручным группам (Steam-коллекции); чипы сверху фильтруют ленту.
/// Пульты пересоздаются при структурных изменениях; правки параметров — на месте.
/// </summary>
public partial class MixerView : UserControl
{
    private const string ProgramsGroupDefault = "Отдельные программы";
    private const string ManualGroupDefault = "Выбранные вручную";
    private const string AddChipTag = "__add";

    private readonly Dictionary<Guid, MixerStrip> _strips = [];

    /// <summary>Device-зона пульта на момент сборки (смена привязки → переезд секции).</summary>
    private readonly Dictionary<Guid, string> _stripZone = [];

    private double _statusAccum;

    /// <summary>Фильтр-чип: null — «Все»; иначе ключ секции (zone:X либо group:id).</summary>
    private string? _filter;

    /// <summary>Кэш «своих» endpoint'ов (PnP-обход) — сбрасывается при смене устройств.</summary>
    private IReadOnlySet<string>? _ownIds;

    /// <summary>Отложенный пересбор: события во время сборки коалесцируются в один.</summary>
    private bool _rebuildPending;

    /// <summary>Блокировка обработчиков на время программного наполнения комбобоксов.</summary>
    private bool _syncingUi;

    /// <summary>Секция ленты: ключ фильтра, заголовок, пульты, признак ручной группы.</summary>
    private sealed record SectionInfo(string Key, string Title, List<AudioNode> Nodes, bool Manual);

    public MixerView()
    {
        InitializeComponent();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        EnsureDefaultGroups();
        AppServices.Graph.Changed += OnGraphChanged;
        AppServices.Devices.DevicesChanged += OnDevicesChanged;
        RenderTicker.Subscribe(OnTick);
        RefreshRateList();
        RefreshMonitorList();
        PushSettingsToEngine();
        RebuildStrips();
        RefreshStatus();
    }

    /// <summary>Первое открытие микшера: стандартная пара ручных групп
    /// (пустой список после удаления всех групп не пере-создаёт их).</summary>
    private void EnsureDefaultGroups()
    {
        var graph = AppServices.Graph;
        if (graph.GroupsInitialized)
        {
            return;
        }

        graph.AddGroup(ProgramsGroupDefault, autoFill: true);
        graph.AddGroup(ManualGroupDefault);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        AppServices.Graph.Changed -= OnGraphChanged;
        AppServices.Devices.DevicesChanged -= OnDevicesChanged;
        RenderTicker.Unsubscribe(OnTick);
    }

    /// <summary>Настройки из AppSettings → движок (идемпотентно: движок не трогает то, что уже так настроено).</summary>
    private static void PushSettingsToEngine()
    {
        AppServices.Engine.SetSampleRate(EngineFormat.ParseSetting(AppServices.Settings.EngineSampleRate));
        AppServices.Engine.SetMonitorDevice(AppServices.Settings.MonitorDeviceId);
    }

    private void RefreshRateList()
    {
        _syncingUi = true;
        try
        {
            RateCombo.Items.Clear();
            RateCombo.Items.Add(new ComboBoxItem { Content = "Авто", Tag = null });
            foreach (var rate in EngineFormat.Rates)
            {
                RateCombo.Items.Add(new ComboBoxItem { Content = FormatRate(rate), Tag = rate });
            }

            var configured = EngineFormat.ParseSetting(AppServices.Settings.EngineSampleRate);
            SelectByTag(RateCombo, configured);
        }
        finally
        {
            _syncingUi = false;
        }
    }

    private void RefreshMonitorList()
    {
        _syncingUi = true;
        try
        {
            var selected = AppServices.Settings.MonitorDeviceId;
            MonitorCombo.Items.Clear();
            MonitorCombo.Items.Add(new ComboBoxItem { Content = "Выкл.", Tag = null });
            foreach (var device in AppServices.Devices.GetDevices(DeviceFlow.Render))
            {
                MonitorCombo.Items.Add(new ComboBoxItem { Content = device.Name, Tag = device.Id });
            }

            if (SelectByTag(MonitorCombo, selected) < 0)
            {
                // Устройство исчезло — оставляем «Выкл.», настройку перезапишет пользователь.
                MonitorCombo.SelectedIndex = 0;
            }
        }
        finally
        {
            _syncingUi = false;
        }
    }

    /// <returns>Индекс найденного элемента или −1.</returns>
    private static int SelectByTag(ComboBox combo, object? tag)
    {
        for (var i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is ComboBoxItem item && Equals(item.Tag, tag))
            {
                combo.SelectedIndex = i;
                return i;
            }
        }

        return -1;
    }

    private static string FormatRate(int rate) =>
        rate % 1000 == 0 ? $"{rate / 1000} кГц" : $"{rate / 1000.0:0.#} кГц";

    private void OnRateChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingUi)
        {
            return;
        }

        var tag = (RateCombo.SelectedItem as ComboBoxItem)?.Tag;
        var rate = tag is int value ? (int?)value : null;
        AppServices.Settings.EngineSampleRate = rate?.ToString(CultureInfo.InvariantCulture) ?? "auto";
        AppServices.Settings.Save();
        AppServices.Engine.SetSampleRate(rate);
    }

    private void OnMonitorChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingUi)
        {
            return;
        }

        var deviceId = (MonitorCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        AppServices.Settings.MonitorDeviceId = deviceId ?? string.Empty;
        AppServices.Settings.Save();
        AppServices.Engine.SetMonitorDevice(deviceId);
    }

    private void OnDevicesChanged(object? sender, EventArgs e)
    {
        _ownIds = null;
        Dispatcher.InvokeAsync(() => RefreshMonitorList());
    }

    private void OnGraphChanged(object? sender, GraphChange e)
    {
        switch (e.Kind)
        {
            case GraphChangeKind.NodeAdded:
                Autofill(e.Node);
                QueueRebuild();
                break;

            case GraphChangeKind.NodeRemoved:
            case GraphChangeKind.Reset:
            case GraphChangeKind.GroupsChanged:
                QueueRebuild();
                break;

            case GraphChangeKind.AppearanceChanged:
                if (e.Node is not null && _strips.TryGetValue(e.Node.Id, out var changedStrip))
                {
                    if (changedStrip.IsFxExpanded != (e.Node.FxExpanded == true))
                    {
                        // Конструкция карточки изменилась (пилюля FX) — пересборка ленты.
                        QueueRebuild();
                    }
                    else
                    {
                        // Высота применяется на месте (иначе сорвался бы drag).
                        changedStrip.ApplyHeight(e.Node.StripHeight);
                    }
                }

                break;

            case GraphChangeKind.NodeChanged:
                if (e.Node is not null
                    && _stripZone.TryGetValue(e.Node.Id, out var builtZone)
                    && !string.Equals(builtZone, DeviceZoneKey(e.Node), StringComparison.Ordinal))
                {
                    // Привязка устройства сменилась — пульт переезжает в другую секцию.
                    QueueRebuild();
                    break;
                }

                foreach (var strip in _strips.Values)
                {
                    strip.RefreshFromNode();
                }

                break;
        }
    }

    /// <summary>Коалесцируем пересбор: события из обработчиков самих пересборов
    /// (GroupsChanged от SetNodeGroup и т.п.) не должны вкладываться рекурсивно.</summary>
    private void QueueRebuild()
    {
        if (_rebuildPending)
        {
            return;
        }

        _rebuildPending = true;
        Dispatcher.InvokeAsync(() =>
        {
            _rebuildPending = false;
            RebuildStrips();
        });
    }

    /// <summary>Новый узел-источник без привязки поглощается первой autofill-группой
    /// («Отдельные программы»: программные входы рождаются без device).</summary>
    private void Autofill(AudioNode? node)
    {
        if (node is null || node.Kind != NodeKind.Source || !string.IsNullOrEmpty(node.DeviceId))
        {
            return;
        }

        var graph = AppServices.Graph;
        if (graph.FindGroupOf(node.Id) is not null)
        {
            return;
        }

        var target = graph.Groups.FirstOrDefault(g => g.AutoFill);
        if (target is not null)
        {
            graph.SetNodeGroup(node.Id, target.Id);
        }
    }

    private void RebuildStrips()
    {
        StripsHost.Children.Clear();
        _strips.Clear();
        _stripZone.Clear();

        var own = OwnIds();
        var sections = BuildSections(own);

        foreach (var section in sections)
        {
            if (_filter is not null)
            {
                if (section.Key != _filter)
                {
                    continue;
                }
            }
            else if (section.Nodes.Count == 0)
            {
                // «Все»: пустые блоки (в т.ч. ручные группы) не занимают места —
                // место выделяется по надобности: чип-фильтр покажет блок с подсказкой.
                continue;
            }

            var block = new StackPanel
            {
                // Ширина не фиксируется: блок занимает свой «остаток строки»
                // (L-волна) — пульты уходят вниз только при нехватке горизонтали.
                Margin = new Thickness(0, 0, BlocksGridPanel.Gap, BlocksGridPanel.Gap),
            };
            block.Children.Add(new TextBlock
            {
                Text = section.Title,
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = (Brush)(section.Manual
                    ? TryFindResource("Brush.TextDim") ?? Brushes.Gray
                    : TryFindResource("Brush.TextFaint") ?? Brushes.Gray),
                Margin = new Thickness(0, 0, 0, 8),
            });

            var wrap = new WrapPanel { Orientation = Orientation.Horizontal };
            foreach (var node in section.Nodes)
            {
                var strip = CreateStrip(node);
                _strips[node.Id] = strip;
                _stripZone[node.Id] = DeviceZoneKey(node);
                wrap.Children.Add(strip);
            }

            if (section.Nodes.Count == 0)
            {
                wrap.Children.Add(new TextBlock
                {
                    Text = section.Manual
                        ? "Пусто — ПКМ по стрипу → «В группу»"
                        : "Нет пультов в этой зоне",
                    FontSize = 11,
                    Foreground = (Brush)(TryFindResource("Brush.TextFaint") ?? Brushes.Gray),
                    VerticalAlignment = VerticalAlignment.Center,
                });
            }

            block.Children.Add(wrap);
            StripsHost.Children.Add(block);
        }

        BuildChips(sections);
    }

    /// <summary>Секции ленты в порядке: ввод → кабели → прочее → ручные группы → выводы.
    /// Узел из ручной группы показывается только в ней (авто-зона его пропускает).</summary>
    private List<SectionInfo> BuildSections(IReadOnlySet<string> own)
    {
        var graph = AppServices.Graph;
        var manual = new Dictionary<Guid, string>();
        foreach (var group in graph.Groups)
        {
            foreach (var id in group.NodeIds)
            {
                if (graph.FindNode(id) is not null)
                {
                    manual[id] = group.Id;
                }
            }
        }

        var ordered = OrderedNodes().ToList();
        var sections = new List<SectionInfo>();

        void AddZone(MixerZone zone)
        {
            var nodes = ordered
                .Where(n => !manual.ContainsKey(n.Id) && MixerZoneInfo.Of(n, own) == zone)
                .ToList();
            sections.Add(new SectionInfo("zone:" + zone, MixerZoneInfo.Title(zone), nodes, false));
        }

        AddZone(MixerZone.Inputs);
        AddZone(MixerZone.VirtualCables);
        AddZone(MixerZone.Unbound);

        foreach (var group in graph.Groups)
        {
            var nodes = ordered.Where(n => group.NodeIds.Contains(n.Id)).ToList();
            sections.Add(new SectionInfo("group:" + group.Id, group.Name, nodes, true));
        }

        AddZone(MixerZone.Outputs);
        return sections;
    }

    private MixerStrip CreateStrip(AudioNode node)
    {
        var strip = new MixerStrip(node, node.FxExpanded == true);
        strip.MuteToggled += s => AppServices.Graph.SetNodeMute(s.Node.Id, !s.Node.Mute);
        strip.SoloToggled += s => AppServices.Graph.SetNodeSolo(s.Node.Id, !s.Node.Solo);
        strip.BypassToggled += s => AppServices.Graph.SetNodeBypass(s.Node.Id, !s.Node.Bypassed);
        strip.GainChanged += (s, gain) => AppServices.Graph.SetNodeGain(s.Node.Id, gain);
        strip.RenameRequested += OnRenameRequested;
        strip.DeleteRequested += OnDeleteRequested;
        strip.AssignGroupRequested += (_, groupId) => AppServices.Graph.SetNodeGroup(strip.Node.Id, groupId);
        strip.ContextMenu!.Opened += (_, _) => PopulateGroupMenu(strip);
        strip.HeightCommitted += s =>
            AppServices.Graph.SetNodeStripHeight(s.Node.Id, (int)Math.Round(s.Height));
        strip.FxToggleRequested += (s, fx, on) => AppServices.Graph.SetNodeFx(s.Node.Id, fx, on);
        strip.FxSettingsRequested += (s, fx) =>
            EffectSettingsWindow.Show(Window.GetWindow(this), s.Node, fx);
        strip.FxModeToggled += (s, expanded) =>
            AppServices.Graph.SetNodeFxExpanded(s.Node.Id, expanded);
        return strip;
    }

    private string DeviceZoneKey(AudioNode node) => "zone:" + MixerZoneInfo.Of(node, OwnIds());

    private IReadOnlySet<string> OwnIds() => _ownIds ??= CableService.GetOwnEndpointIds();

    private static IEnumerable<AudioNode> OrderedNodes() =>
        AppServices.Graph.Nodes.OrderBy(n => n.Kind switch
        {
            NodeKind.Source => 0,
            NodeKind.Bus => 1,
            _ => 2,
        });

    // ── Чипы-фильтры ─────────────────────────────────────────────────────

    private void BuildChips(IReadOnlyList<SectionInfo> sections)
    {
        if (_filter is not null && sections.All(s => s.Key != _filter))
        {
            _filter = null; // группа удалена — возвращаемся к «Все».
        }

        ChipsHost.Children.Clear();

        ChipsHost.Children.Add(MakeChip(
            "all", "Все", sections.Sum(s => s.Nodes.Count), "Все пульты, разложенные по зонам"));

        foreach (var zone in new[]
        {
            MixerZone.Inputs, MixerZone.VirtualCables, MixerZone.Unbound, MixerZone.Outputs,
        })
        {
            var key = "zone:" + zone;
            var count = sections.FirstOrDefault(s => s.Key == key)?.Nodes.Count ?? 0;
            if (count == 0)
            {
                // Пустая авто-зона не тратит ширину строки чипов: фильтровать нечего
                // (в ленте её блок и так скрыт). Ручные группы показываются всегда.
                continue;
            }

            ChipsHost.Children.Add(MakeChip(key, MixerZoneInfo.Label(zone), count, ZoneTooltip(zone)));
        }

        foreach (var section in sections.Where(s => s.Manual))
        {
            var chip = MakeChip(
                section.Key,
                section.Title,
                section.Nodes.Count,
                "Ручная группа · ПКМ — переименовать или удалить");
            AttachGroupChipMenu(chip, section.Key);
            ChipsHost.Children.Add(chip);
        }

        ChipsHost.Children.Add(MakeChip(AddChipTag, "+ группа", 0, "Создать ручную группу"));
    }

    private ToggleButton MakeChip(string key, string title, int count, string tooltip)
    {
        var isAdd = key == AddChipTag;
        var chip = new ToggleButton
        {
            Style = (Style)FindResource("Chip")!,
            Content = isAdd ? title : $"{title} ({count})",
            Tag = key,
            IsChecked = !isAdd && (_filter is null ? key == "all" : key == _filter),
            Margin = new Thickness(0, 0, 8, 0),
            ToolTip = tooltip,
        };
        chip.Click += OnChipClick;
        return chip;
    }

    private void OnChipClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton chip || chip.Tag is not string key)
        {
            return;
        }

        if (key == AddChipTag)
        {
            chip.IsChecked = false;
            NewGroup();
            return;
        }

        _filter = key == "all" ? null : key;
        RebuildStrips();
    }

    private static string ZoneTooltip(MixerZone zone) => zone switch
    {
        MixerZone.Inputs => "Источники на реальных устройствах: микрофоны, захват, loopback",
        MixerZone.VirtualCables => "Источники из наших виртуальных кабелей (Parrhesia In/Out)",
        MixerZone.Unbound => "Источники без привязки устройства",
        _ => "Шины и назначения — итоговая маршрутизация звука",
    };

    // ── Ручные группы ────────────────────────────────────────────────────

    /// <summary>ПКМ по чипу группы: переименование/удаление (узлы остаются в графе).</summary>
    private void AttachGroupChipMenu(FrameworkElement chip, string groupKey)
    {
        var menu = new ContextMenu { Placement = PlacementMode.MousePoint, MinWidth = 160 };

        var rename = new MenuItem { Header = "Переименовать" };
        rename.Click += (_, _) =>
        {
            var group = AppServices.Graph.Groups.FirstOrDefault(g => "group:" + g.Id == groupKey);
            if (group is null)
            {
                return;
            }

            var owner = Window.GetWindow(this);
            if (PromptDialog.Show(owner, "Переименовать группу", group.Name, out var name)
                && !string.IsNullOrWhiteSpace(name))
            {
                AppServices.Graph.RenameGroup(group.Id, name.Trim());
            }
        };

        var remove = new MenuItem { Header = "Удалить" };
        remove.Click += (_, _) =>
        {
            var group = AppServices.Graph.Groups.FirstOrDefault(g => "group:" + g.Id == groupKey);
            if (group is not null)
            {
                AppServices.Graph.RemoveGroup(group.Id);
            }
        };

        menu.Items.Add(rename);
        menu.Items.Add(remove);
        chip.ContextMenu = menu;
    }

    /// <summary>Наполнение «В группу ▸» при каждом открытии меню пульта.</summary>
    private void PopulateGroupMenu(MixerStrip strip)
    {
        var graph = AppServices.Graph;
        strip.GroupMenuItem.Items.Clear();
        foreach (var group in graph.Groups)
        {
            var item = new MenuItem
            {
                Header = group.Name,
                IsCheckable = true,
                IsChecked = group.NodeIds.Contains(strip.Node.Id),
            };
            var id = group.Id;
            item.Click += (_, _) => graph.SetNodeGroup(strip.Node.Id, id);
            strip.GroupMenuItem.Items.Add(item);
        }

        strip.GroupMenuItem.Items.Add(new Separator());
        var add = new MenuItem { Header = "Новая группа…" };
        add.Click += (_, _) => NewGroup(strip);
        strip.GroupMenuItem.Items.Add(add);
        strip.GroupMenuItem.IsEnabled = true;
        strip.UngroupMenuItem.IsEnabled = graph.FindGroupOf(strip.Node.Id) is not null;
    }

    /// <summary>Создание ручной группы; <paramref name="forStrip"/> — сразу положить в неё пульт.</summary>
    private void NewGroup(MixerStrip? forStrip = null)
    {
        var owner = Window.GetWindow(this);
        if (!PromptDialog.Show(owner, "Новая группа", string.Empty, out var name)
            || string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var group = AppServices.Graph.AddGroup(name.Trim());
        if (forStrip is not null)
        {
            AppServices.Graph.SetNodeGroup(forStrip.Node.Id, group.Id);
        }
    }

    private void OnRenameRequested(MixerStrip strip)
    {
        var owner = Window.GetWindow(this);
        if (PromptDialog.Show(owner, "Переименовать", strip.Node.Name, out var name))
        {
            if (!string.Equals(name, strip.Node.Name, StringComparison.Ordinal))
            {
                AppServices.Graph.RenameNode(strip.Node.Id, name);
            }
        }
    }

    private void OnDeleteRequested(MixerStrip strip)
    {
        var answer = MessageBox.Show(
            $"Удалить «{strip.Node.Name}» со всеми связями?",
            "Микшер",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (answer == MessageBoxResult.Yes)
        {
            AppServices.Graph.RemoveNode(strip.Node.Id);
        }
    }

    private void OnTick(double now, double dt)
    {
        foreach (var strip in _strips.Values)
        {
            var peak = LedMeterControl.NormalizePeak(AppServices.Engine.GetPeak(strip.Node.Id));
            strip.UpdateMeter(peak, now, dt);
        }

        _statusAccum += dt;
        if (_statusAccum >= 1.0)
        {
            _statusAccum = 0;
            RefreshStatus();
        }
    }

    private void RefreshStatus()
    {
        var status = AppServices.Engine.Status;
        if (!status.IsRunning)
        {
            StatusText.Text = "Движок: остановлен";
            return;
        }

        var outputs = status.SinkNames.Count > 0
            ? string.Join(" + ", status.SinkNames)
            : status.SinkName;

        var monitor = status.MonitorActive
            ? $" · монитор «{status.MonitorName}»"
            : string.IsNullOrEmpty(AppServices.Settings.MonitorDeviceId)
                ? string.Empty
                : " · монитор выкл.";

        StatusText.Text = $"Движок: работает · {status.SampleRate} Гц · {status.Channels} к · " +
                          $"выходы «{outputs}» · xrun под/переп. {status.UnderrunSamples}/{status.OverflowSamples}" +
                          monitor;
    }
}
