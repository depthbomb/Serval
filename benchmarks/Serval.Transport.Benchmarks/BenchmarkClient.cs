using System.Diagnostics;
using System.Net;

namespace Serval.Transport.Benchmarks;

internal sealed record Workload(string Name, string Path, int Concurrency, byte[] ExpectedBody, bool Post = false);
internal sealed record ClientResult(long Requests, long Errors, double Seconds, double RequestsPerSecond, double P50Milliseconds,
    double P95Milliseconds, double P99Milliseconds, double CpuMicrosecondsPerRequest, string? FirstError);

internal static class BenchmarkClient
{
    public static async Task<ClientResult> MeasureAsync(HttpClient client, Workload workload, double seconds)
    {
        using var process = Process.GetCurrentProcess();
        var cpuBefore = process.TotalProcessorTime;
        var start = Stopwatch.GetTimestamp();
        var deadline = start + (long)(seconds * Stopwatch.Frequency);
        var workers = Enumerable.Range(0, workload.Concurrency).Select(_ => WorkerAsync()).ToArray();
        var results = await Task.WhenAll(workers);
        var elapsed = Stopwatch.GetElapsedTime(start).TotalSeconds;
        var cpu = (process.TotalProcessorTime - cpuBefore).TotalMicroseconds;
        var samples = results.SelectMany(result => result.Samples).ToArray();
        Array.Sort(samples);
        return new ClientResult(samples.LongLength, results.Sum(result => result.Errors), elapsed,
            samples.LongLength / elapsed, Percentile(0.50), Percentile(0.95), Percentile(0.99),
            cpu / Math.Max(1, samples.LongLength), results.Select(result => result.FirstError).FirstOrDefault(error => error is not null));

        double Percentile(double percentile) => samples.Length == 0 ? 0 :
            samples[Math.Min(samples.Length - 1, (int)Math.Ceiling(samples.Length * percentile) - 1)] * 1000d / Stopwatch.Frequency;

        async Task<(List<long> Samples, long Errors, string? FirstError)> WorkerAsync()
        {
            var samples = new List<long>();
            long errors = 0;
            string? firstError = null;
            while (Stopwatch.GetTimestamp() < deadline)
            {
                var started = Stopwatch.GetTimestamp();
                try
                {
                    using var request = new HttpRequestMessage(workload.Post ? HttpMethod.Post : HttpMethod.Get, workload.Path)
                    {
                        Version = client.DefaultRequestVersion,
                        VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                        Content = workload.Post ? new ByteArrayContent(workload.ExpectedBody) : null
                    };
                    using var response = await client.SendAsync(request);
                    var body = await response.Content.ReadAsByteArrayAsync();
                    if (response.StatusCode != HttpStatusCode.OK || response.Version != client.DefaultRequestVersion ||
                        !body.AsSpan().SequenceEqual(workload.ExpectedBody))
                    {
                        throw new InvalidOperationException($"Invalid response: {response.StatusCode}, HTTP/{response.Version}, {body.Length} bytes.");
                    }

                    samples.Add(Stopwatch.GetTimestamp() - started);
                }
                catch (Exception exception)
                {
                    errors++;
                    firstError ??= exception.ToString();
                }
            }

            return (samples, errors, firstError);
        }
    }
}
