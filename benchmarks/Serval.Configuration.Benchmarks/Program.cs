using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Serval.Configuration;

namespace Serval.Configuration.Benchmarks;

public static class Program
{
    private sealed record Measurement(string Name, int Iterations, double MedianNanoseconds, double MinNanoseconds,
                                      double MaxNanoseconds, double AllocatedBytes);

    private static int _sink;

    public static void Main(string[] args)
    {
        var results = new List<Measurement>();
        foreach (var count in new[] { 32, 512, 4_096 })
        {
            var content = new StringBuilder();
            for (var index = 0; index < count; index++)
            {
                content.AppendLine($"  Section{index / 8}__Value{index % 8} = value-{index} # comment");
            }

            var bytes = Encoding.UTF8.GetBytes(content.ToString());
            using var stream = new MemoryStream(bytes);
            using var provider = new DotEnvConfigurationProvider(new DotEnvConfigurationSource
            {
                Path = "benchmark.env",
                FileProvider = new NullFileProvider(),
            });
            var key = $"Section{(count - 1) / 8}:Value{(count - 1) % 8}";
            results.Add(Measure($"parse-{count}", () =>
            {
                stream.Position = 0;
                provider.Load(stream);
                if (!provider.TryGet(key, out var value) || value != $"value-{count - 1}")
                {
                    throw new InvalidOperationException("Parser benchmark produced incorrect data.");
                }
            }));
        }

        var directory = Path.Combine(Path.GetTempPath(), "serval-configuration-benchmark-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, ".env"), "Server__Port=8120");
        var settings = new HostApplicationBuilderSettings { DisableDefaults = true, ContentRootPath = directory };
        var options = new DotEnvConfigurationOptions { ReloadOnChange = false, LoadEnvironmentFile = false };
        var data = new Dictionary<string, string?>();
        for (var index = 0; index < 512; index++)
        {
            data.Add($"FIELD_{index}", index.ToString());
            options.KeyMappings.Add($"FIELD_{index}", $"Group{index / 8}:Value{index % 8}");
        }

        var builder = Host.CreateApplicationBuilder(settings);
        builder.Configuration.AddInMemoryCollection(data);
        builder.UseDotEnvConfiguration(options);
        using (builder.Configuration)
        {
            var section = builder.Configuration.GetSection("Group32");
            results.Add(Measure("mapped-section-512", () =>
            {
                var children = section.GetChildren().ToArray();
                if (children.Length != 8 || children[0].Value != "256")
                {
                    throw new InvalidOperationException("Mapped section benchmark produced incorrect data.");
                }
                _sink = children.Length;
            }));
            results.Add(Measure("mapped-lookup-512", () =>
            {
                var value = builder.Configuration["Group32:Value0"];
                if (value != "256")
                {
                    throw new InvalidOperationException("Mapped lookup benchmark produced incorrect data.");
                }
                _sink = value.Length;
            }));
        }

        results.Add(Measure("host-setup-512", () =>
        {
            var host = Host.CreateApplicationBuilder(settings);
            using (host.Configuration)
            {
                host.Configuration.AddInMemoryCollection(data);
                host.UseDotEnvConfiguration(options);
                if (host.Configuration["Group32:Value0"] != "256")
                {
                    throw new InvalidOperationException("Host benchmark produced incorrect data.");
                }
            }
        }));

        var json = JsonSerializer.Serialize(new
        {
            Runtime = RuntimeInformation.FrameworkDescription,
            OS = RuntimeInformation.OSDescription,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            Samples = 9,
            Results = results,
        }, new JsonSerializerOptions { WriteIndented = true });
        if (args.Length == 1)
        {
            File.WriteAllText(args[0], json);
        }
        Console.WriteLine(json);
        GC.KeepAlive(_sink);
    }

    private static Measurement Measure(string name, Action action)
    {
        var warmup = Stopwatch.StartNew();
        var iterations = 0;
        do
        {
            action();
            iterations++;
        } while (warmup.ElapsedMilliseconds < 400);

        iterations = Math.Clamp(iterations / 4, 8, 1_000_000);
        var times = new double[9];
        var allocations = new double[9];
        for (var sample = 0; sample < times.Length; sample++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            var start = Stopwatch.GetTimestamp();
            for (var iteration = 0; iteration < iterations; iteration++)
            {
                action();
            }
            times[sample] = Stopwatch.GetElapsedTime(start).TotalNanoseconds / iterations;
            allocations[sample] = (double)(GC.GetAllocatedBytesForCurrentThread() - allocated) / iterations;
        }

        Array.Sort(times);
        Array.Sort(allocations);
        return new Measurement(name, iterations, times[4], times[0], times[^1], allocations[4]);
    }
}
