using Parrhesia.Audio.Devices;

namespace Parrhesia.Audio.Tests.Devices;

/// <summary>Ф4: восстановление кастомных имён кабелей после rebind драйвера.</summary>
public class CableNamePolicyTests
{
    [Fact]
    public void ShouldRestore_SystemGenericName()
    {
        // После rebind Windows ставит системное имя (локализованное/английское).
        Assert.True(CableNamePolicy.ShouldRestore("Динамики", 1, isIn: true, "Браузер"));
        Assert.True(CableNamePolicy.ShouldRestore("Speakers", 1, isIn: false, "Микс в игру"));
    }

    [Fact]
    public void ShouldRestore_AutoLaneName()
    {
        // ApplyVirtualEndpointNames поставил авто-имя — вернуть custom.
        Assert.True(CableNamePolicy.ShouldRestore("Parrhesia In1", 1, isIn: true, "хуй1"));
        Assert.True(CableNamePolicy.ShouldRestore("Parrhesia Out2", 2, isIn: false, "Вирт. микрофон"));
    }

    [Fact]
    public void ShouldRestore_ForeignManualRename_NotTouched()
    {
        // Переименование вне приложения не перетираем (другое имя — другой смысл).
        Assert.False(CableNamePolicy.ShouldRestore("Мой вход", 1, isIn: true, "хуй1"));
    }

    [Fact]
    public void ShouldRestore_AlreadyCustom_Skipped()
    {
        Assert.False(CableNamePolicy.ShouldRestore("хуй1", 1, isIn: true, "хуй1"));
    }

    [Fact]
    public void ShouldRestore_NoCustomName_Skipped()
    {
        Assert.False(CableNamePolicy.ShouldRestore("Динамики", 1, isIn: true, null));
        Assert.False(CableNamePolicy.ShouldRestore("Динамики", 1, isIn: true, "  "));
    }

    [Fact]
    public void ShouldRestore_AutoNameOfAnotherLane_Skipped()
    {
        // Авто-имя соседнего лана не «наше» — восстанавливать нельзя.
        Assert.False(CableNamePolicy.ShouldRestore("Parrhesia In2", 1, isIn: true, "хуй1"));
    }
}
