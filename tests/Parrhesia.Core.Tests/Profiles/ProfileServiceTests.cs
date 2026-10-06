using Parrhesia.Core.Graph;
using Parrhesia.Core.Profiles;

namespace Parrhesia.Core.Tests.Profiles;

public class ProfileServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "parrhesia-profiles-" + Guid.NewGuid().ToString("N"));

    private string ProfilesDir => Path.Combine(_root, "profiles");

    private string LegacyPresetsDir => Path.Combine(_root, "presets");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    [Fact]
    public void FirstRun_CreateFromLive_ActivateInitial_AppliesGraph()
    {
        var live = new AudioGraph();
        var service = CreateService(live);

        var demo = new AudioGraph();
        demo.AddNode("Микрофон", NodeKind.Source);
        demo.AddNode("Выход", NodeKind.Sink);
        live.ReplaceWith(demo);
        service.CreateFromLive("Основной");

        var freshLive = new AudioGraph();
        var restarted = CreateService(freshLive);
        Assert.True(restarted.ActivateInitial(out var error), error);
        Assert.Equal(2, freshLive.Nodes.Count);
        Assert.Equal("Основной", restarted.Active?.Name);
    }

    [Fact]
    public void SwitchAway_CapturesEdits_SwitchBack_RestoresThem()
    {
        var live = new AudioGraph();
        var service = CreateService(live);

        var first = service.CreateFromLive("Первый");
        var second = service.CreateEmpty("Второй");
        Assert.True(service.ActivateInitial(out var error), error);
        Assert.Same(first, service.Active);

        // Правим активный профиль «Первый».
        live.AddNode("Новый узел", NodeKind.Bus);

        // Уход: правки уехали в «Первый», активируется пустой «Второй».
        Assert.True(service.SwitchTo(second, out error), error);
        Assert.Empty(live.Nodes);
        Assert.Same(second, service.Active);

        // Правим «Второй» и сохраняем явно (в приложении это делает автосейв).
        live.AddNode("Во втором", NodeKind.Sink);
        service.SaveActive();

        // Возврат: правки обоих профилей на месте.
        Assert.True(service.SwitchTo(first, out error), error);
        Assert.Single(live.Nodes);
        Assert.Equal("Новый узел", live.Nodes[0].Name);

        Assert.True(service.SwitchTo(second, out error), error);
        Assert.Single(live.Nodes);
        Assert.Equal("Во втором", live.Nodes[0].Name);
        service.SaveActive();
    }

    [Fact]
    public void Switch_CapturesStateEvenWhenTargetCorrupt_LiveUntouched()
    {
        // Готовим «битый» профиль прямо на диске.
        var live = new AudioGraph();
        var service = CreateService(live);
        service.CreateFromLive("Хороший");
        service.CreateEmpty("Битый");
        Assert.True(service.ActivateInitial(out _));
        File.WriteAllText(Path.Combine(ProfilesDir, "Битый.profile.json"), "{ не json");

        // Новый «процесс»: активный — «Хороший».
        var live2 = new AudioGraph();
        var restarted = CreateService(live2);
        Assert.True(restarted.ActivateInitial(out var error), error);
        var good = restarted.Profiles.Single(p => p.Name == "Хороший");
        var broken = restarted.Profiles.Single(p => p.Name == "Битый");
        Assert.True(restarted.SwitchTo(good, out _), "активируем «Хороший»");

        // Правим активный и пытаемся уйти в битый.
        live2.AddNode("Правка", NodeKind.Source);

        Assert.False(restarted.SwitchTo(broken, out error));
        Assert.Contains("JSON", error);
        Assert.Same(good, restarted.Active);
        Assert.Single(live2.Nodes);

        // Правки не потерялись: переключение-дубль в «Хороший» ничего не трогает.
        Assert.True(restarted.SwitchTo(good, out _));
        Assert.Single(live2.Nodes);
        Assert.Single(live2.Nodes);
        Assert.Contains("Правка", good.SnapshotJson);
    }

    [Fact]
    public void Reload_RestoresOrderActiveAndContent()
    {
        var live = new AudioGraph();
        var service = CreateService(live);
        service.CreateFromLive("Альфа");
        service.CreateFromLive("Бета");
        var gamma = service.CreateFromLive("Гамма");
        Assert.True(service.ActivateInitial(out var error), error);
        Assert.Equal(new[] { "Альфа", "Бета", "Гамма" }, service.Profiles.Select(p => p.Name));

        // Правки делаются уже в активной «Гамме» и сохраняются (в приложении — автосейвом).
        Assert.True(service.SwitchTo(gamma, out error), error);
        Assert.Equal("Гамма", service.Active?.Name);
        live.AddNode("Дельта", NodeKind.Sink);
        service.SaveActive();

        // Новый «процесс».
        var live2 = new AudioGraph();
        var reloaded = CreateService(live2);
        Assert.Equal(new[] { "Альфа", "Бета", "Гамма" }, reloaded.Profiles.Select(p => p.Name));
        Assert.True(reloaded.ActivateInitial(out error), error);
        Assert.Equal("Гамма", reloaded.Active?.Name);
        Assert.Single(live2.Nodes);
        Assert.Equal("Дельта", live2.Nodes[0].Name);
    }

    [Fact]
    public void CreateEmpty_ProfileHasNoFile_UntilSaved()
    {
        var live = new AudioGraph();
        var service = CreateService(live);
        var profile = service.CreateEmpty("Черновик");

        Assert.Null(profile.SnapshotJson);
        Assert.False(File.Exists(Path.Combine(ProfilesDir, "Черновик.profile.json")));

        Assert.True(service.ActivateInitial(out var error), error);
        service.SaveActive();

        Assert.True(File.Exists(Path.Combine(ProfilesDir, "Черновик.profile.json")));
        Assert.NotNull(profile.SnapshotJson);
    }

    [Fact]
    public void Rename_ChangesNameAndFile_RejectsConflictsAndEmpty()
    {
        var live = new AudioGraph();
        var service = CreateService(live);
        var a = service.CreateFromLive("А");
        service.CreateFromLive("Б");

        Assert.True(service.Rename(a, "Новое имя", out var error), error);
        Assert.Equal("Новое имя", a.Name);
        Assert.True(File.Exists(Path.Combine(ProfilesDir, "Новое имя.profile.json")));
        Assert.False(File.Exists(Path.Combine(ProfilesDir, "А.profile.json")));

        Assert.False(service.Rename(a, "Б", out error));
        Assert.Contains("существует", error);
        Assert.False(service.Rename(a, "   ", out error));
        Assert.Equal("Новое имя", a.Name);
    }

    [Fact]
    public void Close_Active_SwitchesToNeighbour_RemovesFile()
    {
        var live = new AudioGraph();
        var service = CreateService(live);
        var a = service.CreateFromLive("А");
        var b = service.CreateFromLive("Б");
        var c = service.CreateFromLive("Г");
        Assert.True(service.ActivateInitial(out var error), error);
        Assert.Same(a, service.Active);

        Assert.True(service.Close(b, out error), error);
        Assert.Equal(new[] { "А", "Г" }, service.Profiles.Select(p => p.Name));

        // Закрытие активного переключает на соседа (следующий → Г).
        Assert.True(service.Close(a, out error), error);
        Assert.Same(c, service.Active);
        Assert.Single(service.Profiles);
        Assert.False(File.Exists(Path.Combine(ProfilesDir, "А.profile.json")));

        // Последний закрыть нельзя.
        Assert.False(service.Close(c, out error));
        Assert.Contains("единственный", error);
    }

    [Fact]
    public void LegacyPresets_AreMigratedAsProfiles()
    {
        Directory.CreateDirectory(LegacyPresetsDir);
        var legacyGraph = new AudioGraph();
        legacyGraph.AddNode("Из пресета", NodeKind.Source);
        File.WriteAllText(
            Path.Combine(LegacyPresetsDir, "Старый пресет.json"),
            Core.Serialization.GraphSerializer.Serialize(legacyGraph));

        var live = new AudioGraph();
        var service = CreateService(live);

        Assert.Single(service.Profiles);
        Assert.Equal("Старый пресет", service.Profiles[0].Name);
        Assert.True(service.ActivateInitial(out var error), error);
        Assert.Single(live.Nodes);
    }

    [Fact]
    public void CorruptIndex_FallsBackToFileOrder()
    {
        var live = new AudioGraph();
        var service = CreateService(live);
        service.CreateFromLive("Один");
        service.CreateFromLive("Два");

        File.WriteAllText(Path.Combine(ProfilesDir, "index.json"), "не json");

        var reloaded = CreateService(new AudioGraph());
        Assert.Equal(2, reloaded.Profiles.Count);
        Assert.Contains(reloaded.Profiles, p => p.Name == "Один");
        Assert.Contains(reloaded.Profiles, p => p.Name == "Два");
    }

    private ProfileService CreateService(AudioGraph live) =>
        new(live, ProfilesDir, LegacyPresetsDir);
}
