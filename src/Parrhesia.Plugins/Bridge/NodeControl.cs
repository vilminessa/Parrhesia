using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace Parrhesia.Plugins.Bridge;

/// <summary>
/// S-волна: управляющий канал «хост ↔ процесс-исполнитель» (named pipe,
/// JSON-строки). Аудио идёт через shared memory (<see cref="PluginBridge"/>),
/// каналом идут state/params-запросы (не в hot-path — базис latency не растёт).
/// Односоединение, запрос-ответ; сервер живёт в ребёнке.
/// </summary>
public static class NodeControl
{
    /// <summary>Имя pipe для узла (стабильно, как имена секции/кика моста).</summary>
    public static string PipeName(Guid nodeId) => $"Parrhesia.NodeCtl.{nodeId:N}";

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General);
}

/// <summary>Запрос хоста. Op: ping | getState | setState | latency.</summary>
public sealed class NodeControlRequest
{
    public string Op { get; set; } = "ping";

    /// <summary>Байты state (base64) для setState.</summary>
    public string? Data { get; set; }
}

/// <summary>Ответ исполнителя.</summary>
public sealed class NodeControlResponse
{
    public bool Ok { get; set; }

    public string? Error { get; set; }

    /// <summary>Байты state (base64) для getState.</summary>
    public string? Data { get; set; }

    /// <summary>Числовое значение (latency и т.п.).</summary>
    public int Value { get; set; }
}

/// <summary>
/// Серверская сторона (в ребёнке): принимает подключения и раздаёт ответы
/// обработчику (обработчик сам синхронизирует доступ к плагину с аудио-циклом).
/// </summary>
public sealed class NodeControlServer : IDisposable
{
    private readonly string _pipeName;
    private readonly Func<NodeControlRequest, NodeControlResponse> _handler;
    private volatile bool _disposed;

    public NodeControlServer(string pipeName, Func<NodeControlRequest, NodeControlResponse> handler)
    {
        _pipeName = pipeName;
        _handler = handler;
    }

    /// <summary>Фоновый цикл: подключение → строки-запросы → ответы; живёт до Dispose.</summary>
    public void Start() => Task.Run(RunLoop);

    private async Task RunLoop()
    {
        while (!_disposed)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                server.WaitForConnection();

                using var reader = new StreamReader(server, Encoding.UTF8);
                await using var writer = new StreamWriter(server, new UTF8Encoding(false)) { AutoFlush = true };

                while (!_disposed && server.IsConnected)
                {
                    var line = await reader.ReadLineAsync();
                    if (line is null)
                    {
                        break;
                    }

                    NodeControlResponse response;
                    try
                    {
                        var request = JsonSerializer.Deserialize<NodeControlRequest>(line, NodeControl.JsonOptions)
                            ?? throw new InvalidOperationException("пустой запрос");
                        response = _handler(request);
                    }
                    catch (Exception ex)
                    {
                        response = new NodeControlResponse { Ok = false, Error = ex.Message };
                    }

                    await writer.WriteLineAsync(
                        JsonSerializer.Serialize(response, NodeControl.JsonOptions));
                }
            }
            catch (Exception)
            {
                if (_disposed)
                {
                    return;
                }

                // Разрыв/ошибка pipe не должны убивать исполнителя — переподключаемся.
                Thread.Sleep(50);
            }
        }
    }

    public void Dispose() => _disposed = true;
}

/// <summary>
/// Клиентская сторона (хост): устанавливает соединение с ребёнком и шлёт
/// запросы. Ошибка/таймаут — IOException/TimeoutException (вызывающий решает).
/// </summary>
public sealed class NodeControlClient : IDisposable
{
    private readonly string _pipeName;
    private readonly object _gate = new();
    private NamedPipeClientStream? _stream;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private bool _disposed;

    public NodeControlClient(string pipeName) => _pipeName = pipeName;

    /// <summary>Запрос-ответ. timeout — общий предел на подключение+обмен.</summary>
    public NodeControlResponse Request(NodeControlRequest request, TimeSpan? timeout = null)
    {
        var limit = timeout ?? TimeSpan.FromSeconds(5);
        var payload = JsonSerializer.Serialize(request, NodeControl.JsonOptions);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Connect(limit);
            _writer!.WriteLine(payload);

            var line = _reader!.ReadLine()
                ?? throw new IOException("исполнитель закрыл управляющий канал");
            return JsonSerializer.Deserialize<NodeControlResponse>(line, NodeControl.JsonOptions)
                ?? throw new IOException("пустой ответ исполнителя");
        }
    }

    private void Connect(TimeSpan limit)
    {
        if (_stream is { IsConnected: true })
        {
            return;
        }

        Drop();

        var deadline = Environment.TickCount64 + (long)limit.TotalMilliseconds;
        while (true)
        {
            try
            {
                var stream = new NamedPipeClientStream(
                    ".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                // Подключение — в пределах общего лимита (ребёнок поднимает pipe после спавна).
                var remain = Math.Max(1, (int)(deadline - Environment.TickCount64));
                stream.Connect(remain);

                _stream = stream;
                _reader = new StreamReader(stream, Encoding.UTF8);
                _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
                return;
            }
            catch (TimeoutException)
            {
                throw new TimeoutException($"исполнитель {_pipeName} не принял канал за {limit.TotalSeconds:0.#} с");
            }
            catch (IOException)
            {
                if (Environment.TickCount64 >= deadline)
                {
                    throw;
                }

                Thread.Sleep(30); // pipe ещё не создан — пробуем до дедлайна
            }
        }
    }

    private void Drop()
    {
        _writer?.Dispose();
        _writer = null;
        _reader?.Dispose();
        _reader = null;
        _stream?.Dispose();
        _stream = null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Drop();
        }
    }
}
