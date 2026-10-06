namespace Parrhesia.Core.Graph;

/// <summary>
/// Карта каналов маршрута: какие пары «выход → вход» соединены.
/// Каналы: 0 = L, 1 = R. Битовая маска: L→L, L→R, R→L, R→R.
/// По умолчанию — <see cref="Direct"/> (L→L и R→R, стерео-пара).
/// </summary>
public readonly struct ChannelMap : IEquatable<ChannelMap>
{
    public const int Left = 0;
    public const int Right = 1;

    private const byte LeftToLeftBit = 1 << 0;
    private const byte LeftToRightBit = 1 << 1;
    private const byte RightToLeftBit = 1 << 2;
    private const byte RightToRightBit = 1 << 3;
    private const byte ValidMask = 0x0F;

    /// <summary>Соединение по прямой: L→L и R→R.</summary>
    public static readonly ChannelMap Direct = new(LeftToLeftBit | RightToRightBit);

    private readonly byte _bits;

    public ChannelMap(byte bits)
    {
        if ((bits & ~ValidMask) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bits), "Допустимы только биты пар каналов (0x0F).");
        }

        _bits = bits;
    }

    public byte Bits => _bits;

    public bool IsEmpty => _bits == 0;

    /// <summary>Карта из одной пары каналов.</summary>
    public static ChannelMap Pair(int fromChannel, int toChannel) => new(Bit(fromChannel, toChannel));

    public bool Has(int fromChannel, int toChannel) => (_bits & Bit(fromChannel, toChannel)) != 0;

    /// <summary>Копия карты с включённой/выключенной парой каналов.</summary>
    public ChannelMap With(int fromChannel, int toChannel, bool enabled)
    {
        var bit = Bit(fromChannel, toChannel);
        return new ChannelMap(enabled
            ? (byte)(_bits | bit)
            : (byte)(_bits & ~bit));
    }

    /// <summary>Все соединённые пары: (канал_источника, канал_назначения).</summary>
    public IEnumerable<(int From, int To)> Pairs()
    {
        for (var from = Left; from <= Right; from++)
        {
            for (var to = Left; to <= Right; to++)
            {
                if (Has(from, to))
                {
                    yield return (from, to);
                }
            }
        }
    }

    private static byte Bit(int fromChannel, int toChannel)
    {
        Validate(fromChannel, nameof(fromChannel));
        Validate(toChannel, nameof(toChannel));
        return (byte)(1 << ((fromChannel * 2) + toChannel));
    }

    private static void Validate(int channel, string parameterName)
    {
        if (channel is < Left or > Right)
        {
            throw new ArgumentOutOfRangeException(parameterName, channel, "Канал: 0 (L) или 1 (R).");
        }
    }

    public bool Equals(ChannelMap other) => _bits == other._bits;

    public override bool Equals(object? obj) => obj is ChannelMap other && Equals(other);

    public override int GetHashCode() => _bits;

    public static bool operator ==(ChannelMap left, ChannelMap right) => left.Equals(right);

    public static bool operator !=(ChannelMap left, ChannelMap right) => !left.Equals(right);

    public override string ToString() =>
        string.Join("|", Pairs().Select(p =>
            (p.From == Left ? "L" : "R") + "→" + (p.To == Left ? "L" : "R")));
}
