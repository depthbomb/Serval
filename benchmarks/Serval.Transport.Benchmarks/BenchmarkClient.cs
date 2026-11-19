using System.Diagnostics;
using System.Net;

namespace Serval.Transport.Benchmarks;

internal sealed record Workload(
    string                               Name,
    string                               Path,
    int                                  Concurrency,
    byte[]                               ExpectedBody,
    bool                                 Post    = false,
    IReadOnlyDictionary<string, string>? Headers = null);

internal sealed record ClientResult(
    long    Requests,
    long    Errors,
    double  Seconds,
    double  RequestsPerSecond,
    double  P50Milliseconds,
    double  P95Milliseconds,
    double  P99Milliseconds,
    double  CpuMicrosecondsPerRequest,
    string? FirstError);

internal static class BenchmarkClient
{
    public static async Task<ClientResult> MeasureAsync(HttpClient client, Workload workload, double seconds)
    {
        using var process        = Process.GetCurrentProcess();
        var       cpuBefore      = process.TotalProcessorTime;
        var       start          = Stopwatch.GetTimestamp();
        var       deadline       = start + (long)(seconds * Stopwatch.Frequency);
        var       workers        = Enumerable.Range(0, workload.Concurrency).Select(_ => WorkerAsync()).ToArray();
        var       results        = await Task.WhenAll(workers);
        var       elapsed        = Stopwatch.GetElapsedTime(start).TotalSeconds;
        var       cpu            = (process.TotalProcessorTime - cpuBefore).TotalMicroseconds;
        var       latencySamples = results.SelectMany(result => result.Samples).ToArray();
        Array.Sort(latencySamples);
        return new ClientResult(latencySamples.LongLength, results.Sum(result => result.Errors), elapsed,
            latencySamples.LongLength / elapsed, Percentile(0.50), Percentile(0.95), Percentile(0.99),
            cpu                       / Math.Max(1, latencySamples.LongLength), results.Select(result => result.FirstError).FirstOrDefault(error => error is not null));

        double Percentile(double percentile)
        {
            return latencySamples.Length == 0 ? 0 : latencySamples[Math.Min(latencySamples.Length - 1, (int)Math.Ceiling(latencySamples.Length * percentile) - 1)] * 1000d / Stopwatch.Frequency;
        }

        async Task<(List<long> Samples, long Errors, string? FirstError)> WorkerAsync()
        {
            var     samples    = new List<long>();
            long    errors     = 0;
            string? firstError = null;
            while (Stopwatch.GetTimestamp() < deadline)
            {
                var started = Stopwatch.GetTimestamp();
                try
                {
                    using var request = new HttpRequestMessage(workload.Post ? HttpMethod.Post : HttpMethod.Get, workload.Path);
                    request.Version       = client.DefaultRequestVersion;
                    request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
                    request.Content       = workload.Post ? new ByteArrayContent(workload.ExpectedBody) : null;
                    if (workload.Headers is not null)
                    {
                        foreach (var (name, value) in workload.Headers)
                        {
                            request.Headers.Add(name, value);
                        }
                    }

                    using var response = await client.SendAsync(request);
                    var       body     = await response.Content.ReadAsByteArrayAsync();
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
