namespace Parrhesia.Core.Graph;

/// <summary>
/// Карта каналов маршрута: какие пары «выход → вход» соединены.
/// Битовая матрица <see cref="MaxChannels"/>×<see cref="MaxChannels"/>,
/// позиция бита = from * MaxChannels + to.
/// Дефолт для маршрута — <see cref="Diagonal"/> (1:1, моно→стерео дубль,
/// стерео→моно деление — как в Cantabile).
/// </summary>
public readonly struct ChannelMap : IEquatable<ChannelMap>
{
    /// <summary>Физическая ёмкость карты. Число каналов ноды ограничено отдельно (см. AudioNode.MaxChannels).</summary>
    public const int MaxChannels = 8;

    /// <summary>Индекс канала 0 (для двухканальных узлов — левый).</summary>
    public const int Left = 0;

    /// <summary>Индекс канала 1 (для двухканальных узлов — правый).</summary>
    public const int Right = 1;

    private readonly ulong _bits;

    public ChannelMap(ulong bits)
    {
        _bits = bits;
    }

    public ulong Bits => _bits;

    public bool IsEmpty => _bits == 0;

    /// <summary>Карта из одной пары каналов.</summary>
    public static ChannelMap Pair(int fromChannel, int toChannel) => new(Bit(fromChannel, toChannel));

    /// <summary>
    /// Карта по умолчанию между портами: равные счётчики — диагональ 1:1;
    /// моно→стерео — дубль во все каналы; стерео→моно — все в один
    /// (масштабирование N→1 делает процессор).
    /// </summary>
    public static ChannelMap Diagonal(int fromCount, int toCount)
    {
        ValidateCount(fromCount, nameof(fromCount));
        ValidateCount(toCount, nameof(toCount));

        ulong bits = 0;
        if (fromCount == toCount)
        {
            for (var i = 0; i < fromCount; i++)
            {
                bits |= Bit(i, i);
            }
        }
        else if (fromCount == 1)
        {
            for (var to = 0; to < toCount; to++)
            {
                bits |= Bit(0, to);
            }
        }
        else if (toCount == 1)
        {
            for (var from = 0; from < fromCount; from++)
            {
                bits |= Bit(from, 0);
            }
        }
        else
        {
            // Разные счётчики >1: 1:1 по пересечению (расширение диапазона позже).
            var common = Math.Min(fromCount, toCount);
            for (var i = 0; i < common; i++)
            {
                bits |= Bit(i, i);
            }
        }

        return new ChannelMap(bits);
    }

    /// <summary>Копия без пар, выходящих за счётчики каналов узлов.</summary>
    public ChannelMap Restrict(int fromCount, int toCount)
    {
        ValidateCount(fromCount, nameof(fromCount));
        ValidateCount(toCount, nameof(toCount));

        ulong bits = 0;
        foreach (var (from, to) in Pairs())
        {
            if (from < fromCount && to < toCount)
            {
                bits |= Bit(from, to);
            }
        }

        return new ChannelMap(bits);
    }

    /// <summary>
    /// Миграция формата v1: старая раскладка 2×2 — позиция бита = from*2 + to.
    /// </summary>
    public static ChannelMap FromLegacyBits(ulong legacyBits)
    {
        ulong bits = 0;
        for (var bit = 0; bit < 4; bit++)
        {
            if ((legacyBits & (1UL << bit)) == 0)
            {
                continue;
            }

            bits |= Bit(bit / 2, bit % 2);
        }

        return new ChannelMap(bits);
    }

    public bool Has(int fromChannel, int toChannel) => (_bits & Bit(fromChannel, toChannel)) != 0;

    /// <summary>Копия карты с включённой/выключенной парой каналов.</summary>
    public ChannelMap With(int fromChannel, int toChannel, bool enabled)
    {
        var bit = Bit(fromChannel, toChannel);
        return new ChannelMap(enabled ? _bits | bit : _bits & ~bit);
    }

    /// <summary>Все соединённые пары: (канал_источника, канал_назначения).</summary>
    public IEnumerable<(int From, int To)> Pairs()
    {
        for (var from = 0; from < MaxChannels; from++)
        {
            for (var to = 0; to < MaxChannels; to++)
            {
                if ((_bits & (1UL << ((from * MaxChannels) + to))) != 0)
                {
                    yield return (from, to);
                }
            }
        }
    }

    private static ulong Bit(int fromChannel, int toChannel)
    {
        Validate(fromChannel, nameof(fromChannel));
        Validate(toChannel, nameof(toChannel));
        return 1UL << ((fromChannel * MaxChannels) + toChannel);
    }

    private static void Validate(int channel, string parameterName)
    {
        if (channel is < 0 or >= MaxChannels)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                channel,
                $"Канал: 0..{MaxChannels - 1}.");
        }
    }

    private static void ValidateCount(int count, string parameterName)
    {
        if (count is < 0 or > MaxChannels)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                count,
                $"Число каналов: 0..{MaxChannels}.");
        }
    }

    public bool Equals(ChannelMap other) => _bits == other._bits;

    public override bool Equals(object? obj) => obj is ChannelMap other && Equals(other);

    public override int GetHashCode() => _bits.GetHashCode();

    public static bool operator ==(ChannelMap left, ChannelMap right) => left.Equals(right);

    public static bool operator !=(ChannelMap left, ChannelMap right) => !left.Equals(right);

    public override string ToString() =>
        string.Join("|", Pairs().Select(p => (p.From + 1) + "→" + (p.To + 1)));
}
