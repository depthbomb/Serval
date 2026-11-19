#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Serval.Jobs.Tests;

public sealed class JobSampleTests
{
    [Fact]
    public async Task SampleControllersInspectAndControlTheSharedRegistryOverHttp()
    {
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        var             host     = Sample.Jobs.Program.Build(["--port", port.ToString(), "--Logging:LogLevel:Default", "None"]);
        await using var lifetime = (IAsyncDisposable)host;
        var             registry = host.Services.GetRequiredService<JobRegistry>();
        registry.Pause("pulse");
        registry.Pause("calendar");
        await host.StartAsync();
        try
        {
            using var client = new HttpClient();
            client.BaseAddress = new Uri($"http://localhost:{port}");
            client.Timeout     = TimeSpan.FromSeconds(10);
            Assert.Contains("Recurring jobs", await client.GetStringAsync("/"));
            using var initial = JsonDocument.Parse(await client.GetStringAsync("/jobs"));
            Assert.Equal(2, initial.RootElement.GetArrayLength());
            Assert.All(initial.RootElement.EnumerateArray(), job => Assert.True(job.GetProperty("isPaused").GetBoolean()));
            Assert.Equal(TimeSpan.FromSeconds(5), registry.GetJob("pulse").Schedule.Period);
            Assert.Equal("*/15 * * * * *", registry.GetJob("calendar").Schedule.Expression!.Expression);

            var       first = client.PostAsync("/jobs/pulse/run", null);
            using var cts   = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!registry.GetJob("pulse").IsRunning)
            {
                await Task.Delay(1, cts.Token);
            }

            using var conflict = await client.PostAsync("/jobs/pulse/run", null, CancellationToken.None);
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
            using var completed = await first;
            Assert.Equal(HttpStatusCode.NoContent, completed.StatusCode);
            using var second = await client.PostAsync("/jobs/pulse/run", null, CancellationToken.None);
            Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);
            Assert.Equal(2, registry.GetJob("pulse").RunCount);

            using var resumed = await client.PostAsync("/jobs/calendar/resume", null, CancellationToken.None);
            Assert.Equal(HttpStatusCode.NoContent, resumed.StatusCode);
            Assert.False(registry.GetJob("calendar").IsPaused);
            using var paused = await client.PostAsync("/jobs/calendar/pause", null, CancellationToken.None);
            Assert.Equal(HttpStatusCode.NoContent, paused.StatusCode);
            Assert.True(registry.GetJob("calendar").IsPaused);
            using var missing = await client.PostAsync("/jobs/missing/run", null, CancellationToken.None);
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }
        finally
        {
            await host.StopAsync();
        }
    }
}
