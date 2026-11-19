using System.Diagnostics;
using System.Net;
using System.Net.Quic;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Serval.Transport.Benchmarks;

internal sealed record Trial(string Variant, string Transport, string Workload, int Concurrency, int Repetition, ClientResult Client,
    double ServerAllocatedBytesPerRequest, double ServerCpuMicrosecondsPerRequest, long ServerWorkingSetBytes, int Gen0, int Gen1, int Gen2);

internal static class BenchmarkRunner
{
    public static async Task RunAsync(string[] args)
    {
        var output = Option("--output") ?? throw new ArgumentException("Supply --output with a JSON path outside the repository.");
        var transports = (Option("--transports") ?? "listener,kestrel1,kestrel2,kestrel3").Split(',');
        var repetitions = int.Parse(Option("--repetitions") ?? "5");
        var seconds = double.Parse(Option("--seconds") ?? "3", System.Globalization.CultureInfo.InvariantCulture);
        var warmup = double.Parse(Option("--warmup") ?? "2", System.Globalization.CultureInfo.InvariantCulture);
        var assembly = Assembly.GetExecutingAssembly().Location;
        var baseline = Option("--baseline");
        if (baseline is not null && !File.Exists(baseline))
        {
            throw new FileNotFoundException("The baseline benchmark assembly does not exist.", baseline);
        }

        var variants = baseline is null ? new[] { ("current", assembly) } : new[] { ("baseline", Path.GetFullPath(baseline)), ("current", assembly) };
        var workloads = new[]
        {
            new Workload("small-sequential", "/small", 1, BenchmarkServer.SmallBody),
            new Workload("small-concurrent", "/small", 64, BenchmarkServer.SmallBody),
            new Workload("headers-concurrent", "/small", 64, BenchmarkServer.SmallBody,
                Headers: Enumerable.Range(1, 12).ToDictionary(index => $"X-Client-{index}", index => $"client-value-{index}")),
            new Workload("echo-64KiB", "/echo", 16, BenchmarkServer.UploadBody, true),
            new Workload("download-256KiB", "/download", 8, BenchmarkServer.DownloadBody)
        };
        if (Option("--workloads") is {} selectedWorkloads)
        {
            var selected = selectedWorkloads.Split(',');
            if (selected.Any(name => !workloads.Any(workload => workload.Name == name)))
            {
                throw new ArgumentException("An unknown workload was selected.");
            }

            workloads = workloads.Where(workload => selected.Contains(workload.Name)).ToArray();
        }

        var results = new List<Trial>();
        var metadata = new
        {
            StartedUtc = DateTimeOffset.UtcNow,
            Runtime = RuntimeInformation.FrameworkDescription,
            OperatingSystem = RuntimeInformation.OSDescription,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            Processors = Environment.ProcessorCount,
            ServerGc = GCSettings.IsServerGC,
            TieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"),
            QuicSupported = QuicListener.IsSupported,
            SecondsPerTrial = seconds,
            WarmupSeconds = warmup,
            Repetitions = repetitions,
            Transports = transports,
            Assembly = assembly,
            BaselineAssembly = baseline,
            Workloads = workloads.Select(workload => workload.Name).ToArray()
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        for (var repetition = 0; repetition < repetitions; repetition++)
        {
            var orderedVariants = repetition % 2 == 0 ? variants : variants.Reverse().ToArray();
            var rotation = repetition % transports.Length;
            var cases = transports.Skip(rotation).Concat(transports.Take(rotation))
                .SelectMany(transport => orderedVariants.Select(variant => (Transport: transport, Variant: variant.Item1, Assembly: variant.Item2))).ToArray();
            foreach (var workload in workloads)
            {
                // Rotate protocols and reverse variants so neither implementation consistently runs first.
                foreach (var testCase in cases)
                {
                    var trial = await RunTrialAsync(testCase.Transport, testCase.Variant, testCase.Assembly, workload, repetition + 1, warmup, seconds);
                    results.Add(trial);
                    await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { Metadata = metadata, Trials = results }, Program.Json));
                    Console.WriteLine($"{repetition + 1}/{repetitions} {testCase.Variant,-8} {testCase.Transport,-10} {workload.Name,-20} {trial.Client.RequestsPerSecond,10:F0} req/s  p95 {trial.Client.P95Milliseconds,8:F3} ms  {trial.Client.Errors} errors");
                    if (trial.Client.Errors != 0)
                    {
                        throw new InvalidOperationException(trial.Client.FirstError);
                    }
                }
            }
        }

        string? Option(string name)
        {
            var index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }
    }

    private static async Task<Trial> RunTrialAsync(string transport, string variant, string assembly, Workload workload, int repetition, double warmup, double seconds)
    {
        var version = transport.EndsWith('3') ? HttpVersion.Version30 : transport.EndsWith('2') ? HttpVersion.Version20 : HttpVersion.Version11;
        using var reservation = new Socket(AddressFamily.InterNetwork, version.Major == 3 ? SocketType.Dgram : SocketType.Stream,
            version.Major == 3 ? ProtocolType.Udp : ProtocolType.Tcp);
        reservation.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)reservation.LocalEndPoint!).Port;
        reservation.Close();
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(assembly);
        start.ArgumentList.Add("server");
        start.ArgumentList.Add(transport);
        start.ArgumentList.Add(port.ToString());
        using var server = Process.Start(start) ?? throw new InvalidOperationException("Server launch failed.");
        var errors = server.StandardError.ReadToEndAsync();
        try
        {
            var ready = await server.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));
            if (ready != "READY")
            {
                throw new InvalidOperationException($"Server did not start: {ready}; {await errors}");
            }

            using var handler = new SocketsHttpHandler
            {
                UseProxy = false,
                MaxConnectionsPerServer = 64,
                EnableMultipleHttp2Connections = true,
                SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true }
            };
            using var client = new HttpClient(handler)
            {
                BaseAddress = new Uri($"{(version.Major == 1 ? "http://localhost" : "https://127.0.0.1")}:{port}"),
                DefaultRequestVersion = version,
                DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
                Timeout = TimeSpan.FromSeconds(15)
            };
            var warmed = await BenchmarkClient.MeasureAsync(client, workload, warmup);
            if (warmed.Errors != 0)
            {
                throw new InvalidOperationException($"Warmup failed: {warmed.FirstError}");
            }

            var before = await MetricsAsync();
            var result = await BenchmarkClient.MeasureAsync(client, workload, seconds);
            var after = await MetricsAsync();
            return new Trial(variant, transport, workload.Name, workload.Concurrency, repetition, result,
                (after.AllocatedBytes - before.AllocatedBytes) / (double)Math.Max(1, result.Requests),
                (after.CpuMilliseconds - before.CpuMilliseconds) * 1000 / Math.Max(1, result.Requests),
                after.WorkingSetBytes, after.Gen0 - before.Gen0, after.Gen1 - before.Gen1, after.Gen2 - before.Gen2);
        }
        finally
        {
            if (!server.HasExited)
            {
                await server.StandardInput.WriteLineAsync("stop");
                await server.StandardInput.FlushAsync();
                try
                {
                    await server.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
                }
                catch (TimeoutException)
                {
                    server.Kill(entireProcessTree: true);
                    await server.WaitForExitAsync();
                }
            }

            var errorText = await errors;
            if (server.ExitCode != 0 || errorText.Length != 0)
            {
                throw new InvalidOperationException($"Server exited with {server.ExitCode}: {errorText}");
            }
        }

        async Task<ServerMetrics> MetricsAsync()
        {
            await server.StandardInput.WriteLineAsync("metrics");
            await server.StandardInput.FlushAsync();
            var line = await server.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            return JsonSerializer.Deserialize<ServerMetrics>(line ?? throw new InvalidOperationException("Server closed metrics channel."))!;
        }
    }
}
