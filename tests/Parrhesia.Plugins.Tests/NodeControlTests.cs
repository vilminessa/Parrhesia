using System.Diagnostics;
using Parrhesia.Plugins.Bridge;

namespace Parrhesia.Plugins.Tests;

/// <summary>
/// S2 (S-волна): управляющий канал «хост ↔ исполнитель» — named pipe,
/// JSON-строки. Аудио идёт через shared memory; каналом — state/params.
/// Сервер живёт в тестовом процессе (hermetic, без спавна App.exe).
/// </summary>
public class NodeControlTests
{
    [Fact]
    public void RoundTrip_PingGetSetState()
    {
        var pipe = UniquePipe();
        byte[]? stored = null;

        using var server = new NodeControlServer(pipe, request => request.Op switch
        {
            "ping" => new NodeControlResponse { Ok = true },
            "getState" => new NodeControlResponse
            {
                Ok = true,
                Data = stored is null ? null : Convert.ToBase64String(stored),
            },
            "setState" => Store(request.Data),
            _ => new NodeControlResponse { Ok = false, Error = $"неизвестная операция: {request.Op}" },
        });
        server.Start();

        using var client = new NodeControlClient(pipe);

        Assert.True(client.Request(new NodeControlRequest { Op = "ping" }).Ok);
        Assert.True(client.Request(new NodeControlRequest
        {
            Op = "setState",
            Data = Convert.ToBase64String([4, 5, 6]),
        }).Ok);

        var got = client.Request(new NodeControlRequest { Op = "getState" });
        Assert.True(got.Ok);
        Assert.Equal(new byte[] { 4, 5, 6 }, Convert.FromBase64String(got.Data!));
        return;

        NodeControlResponse Store(string? data)
        {
            stored = data is null ? null : Convert.FromBase64String(data);
            return new NodeControlResponse { Ok = true };
        }
    }

    [Fact]
    public void UnknownOp_ReturnsError_NotCrash()
    {
        var pipe = UniquePipe();
        using var server = new NodeControlServer(
            pipe, _ => new NodeControlResponse { Ok = false, Error = "так нет" });
        server.Start();

        using var client = new NodeControlClient(pipe);
        var response = client.Request(new NodeControlRequest { Op = "wat" });

        Assert.False(response.Ok);
        Assert.Equal("так нет", response.Error);
    }

    [Fact]
    public void NoServer_FailsWithinBoundedTime()
    {
        using var client = new NodeControlClient(UniquePipe());

        var stopwatch = Stopwatch.StartNew();
        Assert.ThrowsAny<Exception>(
            () => client.Request(new NodeControlRequest { Op = "ping" }, TimeSpan.FromSeconds(2)));

        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(6),
            $"клиент завис на {stopwatch.Elapsed.TotalSeconds:0.#} с без сервера");
    }

    [Fact]
    public void ClientReconnects_AfterDrop()
    {
        var pipe = UniquePipe();
        using var server = new NodeControlServer(pipe, _ => new NodeControlResponse { Ok = true });
        server.Start();

        using (var first = new NodeControlClient(pipe))
        {
            Assert.True(first.Request(new NodeControlRequest { Op = "ping" }).Ok);
        }

        // Сервер должен пересоздать pipe после ухода клиента.
        using var second = new NodeControlClient(pipe);
        Assert.True(second.Request(new NodeControlRequest { Op = "ping" }).Ok);
    }

    private static string UniquePipe() => $"Parrhesia.Test.{Guid.NewGuid():N}";
}
