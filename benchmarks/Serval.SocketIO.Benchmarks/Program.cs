using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serval.Hosting;
using Serval.SocketIO.Internal;
using Serval.Transport.HttpListener;

namespace Serval.SocketIO.Benchmarks;

public static class Program
{
    private static long _consumed;

    public static async Task Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "server")
        {
            var port    = int.Parse(args[1]);
            var builder = Host.CreateApplicationBuilder();
            builder.Logging.ClearProviders();
            builder.Services.AddServal().AddSocketIO(io => io.MapNamespace<EchoHandler>("/"), options =>
            {
                options.MaxQueuedBytes   = 16_000_000;
                options.MaxQueuedPackets = 1024;
            }).AddHttpListener(options =>
            {
                options.Prefixes.Clear();
                options.Prefixes.Add($"http://localhost:{port}/");
            });
            using var host = builder.Build();
            await host.RunAsync();
            return;
        }

        var options = new SocketIOOptions();
        options.Validate();
        object[] json =
        [
            "event", new Message
            {
                Index = 42,
                Text  = new string('x', 128)
            }
        ];
        object[] binary =
        [
            "event", new BinaryMessage
            {
                Data = new byte[4096]
            }
        ];
        using var document = JsonDocument.Parse("[{\"index\":42,\"text\":\"hello\"}]");
        var       results  = new[] { Measure("encode-json", () => _consumed += PacketCodec.Encode(2, "/chat", 5, json, options)[0].Data.Length), Measure("encode-binary-4KiB", () => _consumed += PacketCodec.Encode(2, "/chat", 5, binary, options)[1].Data.Length), Measure("decode-typed-json", () => _consumed += new SocketIOArguments(document.RootElement, [], options.Json).Get<Message>(0)!.Index), };
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            runtime    = RuntimeInformation.FrameworkDescription,
            processors = Environment.ProcessorCount,
            results
        }));
        GC.KeepAlive(_consumed);
    }

    private static object Measure(string name, Action action)
    {
        const int iterations = 20_000;
        for (var index = 0; index < 3000; index++)
        {
            action();
        }

        var elapsed     = new double[7];
        var allocations = new double[7];
        for (var sample = 0; sample < elapsed.Length; sample++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            var before  = GC.GetAllocatedBytesForCurrentThread();
            var started = Stopwatch.GetTimestamp();
            for (var index = 0; index < iterations; index++)
            {
                action();
            }

            elapsed[sample]     = Stopwatch.GetElapsedTime(started).TotalNanoseconds / iterations;
            allocations[sample] = (GC.GetAllocatedBytesForCurrentThread() - before)  / (double)iterations;
        }

        Array.Sort(elapsed);
        Array.Sort(allocations);
        return new
        {
            name,
            nanoseconds = elapsed[3],
            bytes       = allocations[3],
            samples     = elapsed
        };
    }
}

public sealed class EchoHandler : SocketIOHandler
{
    public override async ValueTask OnEventAsync(SocketIOEvent message, CancellationToken ct)
    {
        if (message.Name == "json")
        {
            await message.AcknowledgeAsync(message.Arguments.Get<Message>(0));
        }
        else if (message.Name == "binary")
        {
            await message.AcknowledgeAsync(message.Arguments.Get<BinaryMessage>(0));
        }
    }
}

public sealed class Message
{
    public int    Index { get; set; }
    public string Text  { get; set; } = "";
}

public sealed class BinaryMessage
{
    public byte[] Data { get; set; } = [];
}
