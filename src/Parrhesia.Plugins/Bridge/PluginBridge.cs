using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Parrhesia.Plugins.Bridge;

/// <summary>
/// Аудио-мост «хост ↔ плагин-процесс» (S-волна): именованная shared memory
/// с двумя mailbox-слотами (seqlock) — вход host→child, выход child→host —
/// и именованным событием-«киком» для цикла ребёнка.
/// RT-сторона хоста НИКОГДА не блокируется: публикует вход атомарно и читает
/// последний готовый выход; если плагин-процесс не успел — читается прошлый
/// блок (джиттер вместо ожидания). Ребёнок крутит свой цикл: ждёт событие →
/// вход → обработка → выход.
/// </summary>
public static class PluginBridge
{
    /// <summary>"PBRG".</summary>
    public const int MagicValue = 0x50425247;

    public const int Version = 1;

    /// <summary>Контракт стерео (как у IAudioPlugin).</summary>
    public const int Channels = 2;

    /// <summary>Заголовок секции shared memory (перед буферами).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct Header
    {
        public int Magic;
        public int Version;
        public int MaxBlockFrames;
        public int Channels;

        // Seqlock: нечётное значение = запись в progress; чётное = слот цел.
        public int InSeq;
        public long InTick;   // heartbeat хоста (TickCount64)

        public int OutSeq;
        public long OutTick;  // heartbeat ребёнка

        public long ProcessedBlocks; // счётчик ребёнка (диагностика)

        /// <summary>1 — ребёнок загрузил плагин и готов к блокам.</summary>
        public int Ready;
    }

    /// <summary>Размер секции: заголовок + вход + выход (interleaved f32).</summary>
    public static unsafe long SectionBytes(int maxBlockFrames)
    {
        var buffer = (long)maxBlockFrames * Channels * sizeof(float);
        return sizeof(Header) + buffer * 2;
    }

    public static string SharedName(Guid nodeId) => $"Local\\ParrhesiaBridge-{nodeId:N}";

    public static string KickName(Guid nodeId) => $"Local\\ParrhesiaBridge-{nodeId:N}-kick";

    /// <summary>Сторона хоста (владеет секцией; создаётся ДО запуска ребёнка).</summary>
    public sealed unsafe class HostSide : IDisposable
    {
        private readonly MemoryMappedFile _file;
        private readonly MemoryMappedViewAccessor _view;
        private readonly EventWaitHandle _kick;
        private readonly int _maxBlockFrames;
        private byte* _base;

        public HostSide(Guid nodeId, int maxBlockFrames)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(maxBlockFrames, 1);
            _maxBlockFrames = maxBlockFrames;

            _file = MemoryMappedFile.CreateOrOpen(
                PluginBridge.SharedName(nodeId), PluginBridge.SectionBytes(maxBlockFrames));
            _view = _file.CreateViewAccessor(
                0, PluginBridge.SectionBytes(maxBlockFrames), MemoryMappedFileAccess.ReadWrite);
            _view.SafeMemoryMappedViewHandle.AcquirePointer(ref _base);

            _kick = new EventWaitHandle(false, EventResetMode.AutoReset, PluginBridge.KickName(nodeId));

            var header = HeaderPtr;
            header->Magic = PluginBridge.MagicValue;
            header->Version = PluginBridge.Version;
            header->MaxBlockFrames = maxBlockFrames;
            header->Channels = PluginBridge.Channels;
            header->InSeq = 0;
            header->InTick = 0;
            header->OutSeq = 0;
            header->OutTick = 0;
            header->ProcessedBlocks = 0;
            header->Ready = 0;
        }

        /// <summary>Ребёнок поднял флаг готовности (плагин загружен).</summary>
        public bool IsReady => HeaderPtr->Ready != 0;

        private Header* HeaderPtr => (Header*)_base;

        private float* InputBuffer => (float*)(_base + sizeof(Header));

        private float* OutputBuffer =>
            (float*)(_base + sizeof(Header) + (long)_maxBlockFrames * PluginBridge.Channels * sizeof(float));

        /// <summary>RT-safe: публикует вход (seqlock) и «пинает» ребёнка.</summary>
        public void PublishInput(ReadOnlySpan<float> block)
        {
            var frames = Math.Min(block.Length / PluginBridge.Channels, _maxBlockFrames);
            if (frames <= 0)
            {
                return;
            }

            var bytes = (long)frames * PluginBridge.Channels * sizeof(float);
            var seq = HeaderPtr->InSeq;
            HeaderPtr->InSeq = seq + 1; // нечёт — запись в progress
            Thread.MemoryBarrier();
            fixed (float* source = block)
            {
                Buffer.MemoryCopy(source, InputBuffer, bytes, bytes);
            }

            Thread.MemoryBarrier();
            HeaderPtr->InSeq = seq + 2; // чёт — слот цел
            HeaderPtr->InTick = Environment.TickCount64;
            _kick.Set();
        }

        /// <summary>RT-safe: читает последний готовый выход (false — ребёнок не успел).</summary>
        public bool TryReadOutput(Span<float> destination)
        {
            var bytes = (long)Math.Min(
                destination.Length / PluginBridge.Channels, _maxBlockFrames) * PluginBridge.Channels * sizeof(float);
            if (bytes <= 0)
            {
                return false;
            }

            for (var attempt = 0; attempt < 4; attempt++)
            {
                var seq = HeaderPtr->OutSeq;
                if ((seq & 1) != 0)
                {
                    continue; // пишем — перечитаем
                }

                Thread.MemoryBarrier();
                fixed (float* target = destination)
                {
                    Buffer.MemoryCopy(OutputBuffer, target, bytes, bytes);
                }

                Thread.MemoryBarrier();
                if (HeaderPtr->OutSeq == seq)
                {
                    return true;
                }
            }

            return false; // гонка — читаем в следующем блоке
        }

        /// <summary>Ребёнок подаёт признаки жизни (heartbeat OutTick).</summary>
        public bool ChildAlive(TimeSpan timeout) =>
            Environment.TickCount64 - Interlocked.Read(ref HeaderPtr->OutTick) < timeout.TotalMilliseconds;

        public long ProcessedBlocks => HeaderPtr->ProcessedBlocks;

        public void Dispose()
        {
            _view.SafeMemoryMappedViewHandle.ReleasePointer();
            _base = null;
            _view.Dispose();
            _file.Dispose();
            _kick.Dispose();
        }
    }

    /// <summary>Сторона ребёнка (открывает секцию, созданную хостом).</summary>
    public sealed unsafe class ChildSide : IDisposable
    {
        private readonly MemoryMappedFile _file;
        private readonly MemoryMappedViewAccessor _view;
        private readonly EventWaitHandle _kick;
        private readonly int _maxBlockFrames;
        private byte* _base;

        public ChildSide(Guid nodeId, int maxBlockFrames, TimeSpan startupTimeout)
        {
            _maxBlockFrames = maxBlockFrames;

            var deadline = Environment.TickCount64 + (long)startupTimeout.TotalMilliseconds;
            while (true)
            {
                try
                {
                    _file = MemoryMappedFile.OpenExisting(
                        PluginBridge.SharedName(nodeId), MemoryMappedFileRights.ReadWrite);
                    break;
                }
                catch (FileNotFoundException)
                {
                    if (Environment.TickCount64 >= deadline)
                    {
                        throw;
                    }

                    Thread.Sleep(20);
                }
            }

            _view = _file.CreateViewAccessor(
                0, PluginBridge.SectionBytes(maxBlockFrames), MemoryMappedFileAccess.ReadWrite);
            _view.SafeMemoryMappedViewHandle.AcquirePointer(ref _base);

            _kick = EventWaitHandle.OpenExisting(PluginBridge.KickName(nodeId));
        }

        private Header* HeaderPtr => (Header*)_base;

        /// <summary>Ребёнок сообщает готовность (плагин загружен) — хост ждёт флаг при спавне.
        /// Заодно ставит heartbeat: «готов» означает «жив сейчас».</summary>
        public void MarkReady()
        {
            HeaderPtr->Ready = 1;
            HeaderPtr->OutTick = Environment.TickCount64;
        }

        /// <summary>Признак жизни без обработки блока (дыхание цикла при молчании хоста).</summary>
        public void Heartbeat() => HeaderPtr->OutTick = Environment.TickCount64;

        private float* InputBuffer => (float*)(_base + sizeof(Header));

        private float* OutputBuffer =>
            (float*)(_base + sizeof(Header) + (long)_maxBlockFrames * PluginBridge.Channels * sizeof(float));

        /// <summary>Ждёт «кик» хоста (таймаут — дыхание на случай молчания).</summary>
        public bool WaitForKick(TimeSpan timeout) => _kick.WaitOne(timeout);

        /// <summary>Seqlock-read входа (false — новых блоков нет).</summary>
        public bool TryTakeInput(Span<float> destination)
        {
            var bytes = (long)Math.Min(
                destination.Length / PluginBridge.Channels, _maxBlockFrames) * PluginBridge.Channels * sizeof(float);
            if (bytes <= 0)
            {
                return false;
            }

            for (var attempt = 0; attempt < 4; attempt++)
            {
                var seq = HeaderPtr->InSeq;
                if ((seq & 1) != 0)
                {
                    continue;
                }

                Thread.MemoryBarrier();
                fixed (float* target = destination)
                {
                    Buffer.MemoryCopy(InputBuffer, target, bytes, bytes);
                }

                Thread.MemoryBarrier();
                if (HeaderPtr->InSeq == seq)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Seqlock-write выхода + heartbeat.</summary>
        public void PublishOutput(ReadOnlySpan<float> block)
        {
            var frames = Math.Min(block.Length / PluginBridge.Channels, _maxBlockFrames);
            if (frames <= 0)
            {
                return;
            }

            var bytes = (long)frames * PluginBridge.Channels * sizeof(float);
            var seq = HeaderPtr->OutSeq;
            HeaderPtr->OutSeq = seq + 1;
            Thread.MemoryBarrier();
            fixed (float* source = block)
            {
                Buffer.MemoryCopy(source, OutputBuffer, bytes, bytes);
            }

            Thread.MemoryBarrier();
            HeaderPtr->OutSeq = seq + 2;
            HeaderPtr->OutTick = Environment.TickCount64;
            HeaderPtr->ProcessedBlocks = HeaderPtr->ProcessedBlocks + 1;
        }

        public void Dispose()
        {
            _view.SafeMemoryMappedViewHandle.ReleasePointer();
            _base = null;
            _view.Dispose();
            _file.Dispose();
            _kick.Dispose();
        }
    }
}
