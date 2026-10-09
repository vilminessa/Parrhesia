namespace Parrhesia.Core.Graph;

/// <summary>
/// Ручная группа микшера (Steam-коллекция, M-волна «зоны»): именованный
/// набор узлов одного профиля. Хранится в JSON графа (field groups);
/// ссылки валидны только на узлы этого же графа (мёртвые id вычищаются
/// при загрузке/замене графа). Узел может состоять максимум в одной
/// группе (SetNodeGroup — перенос, чтобы стрип не дублировался в ленте).
/// </summary>
public sealed class MixerGroup
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = string.Empty;

    public List<Guid> NodeIds { get; set; } = [];

    /// <summary>
    /// Автонаполнять новыми узлами-источниками без привязки устройства
    /// («Отдельные программы» — программные входы рождаются без device).
    /// </summary>
    public bool AutoFill { get; set; }
}
