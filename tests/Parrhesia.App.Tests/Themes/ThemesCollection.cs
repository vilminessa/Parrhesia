using Xunit;

namespace Parrhesia.App.Tests.Themes;

/// <summary>Темы делят статику ThemeManager (Override/кэш) — только последовательно.</summary>
[CollectionDefinition("themes", DisableParallelization = true)]
public sealed class ThemesCollection
{
    public const string Name = "themes";
}
