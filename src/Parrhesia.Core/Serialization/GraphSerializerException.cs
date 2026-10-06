namespace Parrhesia.Core.Serialization;

/// <summary>Ошибка разбора файла пресета (JSON, версия, структура).</summary>
public sealed class GraphSerializerException : Exception
{
    public GraphSerializerException(string message)
        : base(message)
    {
    }
}
