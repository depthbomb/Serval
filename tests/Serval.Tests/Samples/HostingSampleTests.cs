#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Serval.Sample.BackgroundServices;
using Serval.Sample.EntityFramework;
using Program = Serval.Sample.EntityFramework.Program;

namespace Serval.Tests.Samples;

public sealed class HostingSampleTests
{
    [Fact]
    public async Task EntityFrameworkSampleInitializesAndPersistsCrudAcrossHostRestarts()
    {
        var    folder     = Directory.CreateTempSubdirectory("serval-ef-sample-").FullName;
        var    connection = ConnectionString(Path.Combine(folder, "notes.db"));
        string location;
        await using (var sample = await RunningSample.StartAsync(Program.Build, "--ConnectionStrings:Notes", connection))
        {
            using var created = await sample.Client.PostAsJsonAsync("/notes", new
            {
                title = "  Persist me  ",
                body  = "First version"
            });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            location = created.Headers.Location!.ToString();
            var note = (await created.Content.ReadFromJsonAsync<Note>())!;
            Assert.Equal("Persist me", note.Title);
            Assert.Equal(location, $"/notes/{note.Id}");

            using var invalid = await sample.Client.PostAsJsonAsync("/notes", new
            {
                title = "",
                body  = "Invalid"
            });
            Assert.Equal(HttpStatusCode.UnprocessableContent, invalid.StatusCode);
            using var oversized = await sample.Client.PostAsJsonAsync("/notes", new
            {
                title = "Invalid",
                body  = new string('x', 4001)
            });
            Assert.Equal(HttpStatusCode.UnprocessableContent, oversized.StatusCode);

            using var replaced = await sample.Client.PutAsJsonAsync(location, new
            {
                title = "Saved",
                body  = "Second version"
            });
            Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);
            var reads = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => sample.Client.GetFromJsonAsync<Note>(location)));
            Assert.All(reads, value => Assert.Equal("Second version", value!.Body));
            Assert.Single((await sample.Client.GetFromJsonAsync<Note[]>("/notes?offset=0&limit=1"))!);
            using var invalidPage = await sample.Client.GetAsync("/notes?limit=0");
            Assert.Equal(HttpStatusCode.BadRequest, invalidPage.StatusCode);
            Assert.Contains("EF Core", await sample.Client.GetStringAsync("/"));
        }

        await using (var sample = await RunningSample.StartAsync(Program.Build, "--ConnectionStrings:Notes", connection))
        {
            var persisted = (await sample.Client.GetFromJsonAsync<Note>(location))!;
            Assert.Equal("Saved", persisted.Title);
            Assert.Equal("Second version", persisted.Body);
            using var deleted = await sample.Client.DeleteAsync(location);
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
            using var missing = await sample.Client.GetAsync(location);
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }
    }

    [Fact]
    public async Task BackgroundSampleLoadsBeforeServingThenRefreshesAndRecoversFromInvalidSource()
    {
        var folder = Directory.CreateTempSubdirectory("serval-catalog-sample-").FullName;
        var source = Path.Combine(folder, "source.json");
        await ReplaceSourceAsync(source, """[{"sku":"coffee","name":"Coffee","priceCents":1200},{"sku":"tea","name":"Tea","priceCents":800}]""");
        await using var sample = await RunningSample.StartAsync(Sample.BackgroundServices.Program.Build,
            "--ConnectionStrings:Catalog", ConnectionString(Path.Combine(folder, "catalog.db")),
            "--Catalog:SourcePath", source, "--Catalog:RefreshInterval", "00:00:00.050");

        var initial = (await sample.Client.GetFromJsonAsync<Product[]>("/catalog"))!;
        Assert.Equal(2, initial.Length);
        Assert.Equal("Coffee", (await sample.Client.GetFromJsonAsync<Product>("/catalog/coffee"))!.Name);
        var state = sample.Host.Services.GetRequiredService<CatalogImportState>();
        Assert.NotNull(state.Current.LastSuccess);
        Assert.Contains("Hosted initialization", await sample.Client.GetStringAsync("/"));
        Assert.Contains("Development", await sample.Client.GetStringAsync("/status"));

        await ReplaceSourceAsync(source, """[{"sku":"coffee","name":"Changed","priceCents":2000},{"sku":"coffee","name":"Duplicate","priceCents":1}]""");
        await EventuallyAsync(() => Task.FromResult(state.Current.LastError is not null));
        Assert.Equal("Coffee", (await sample.Client.GetFromJsonAsync<Product>("/catalog/coffee"))!.Name);
        Assert.Equal(2, (await sample.Client.GetFromJsonAsync<Product[]>("/catalog"))!.Length);

        await ReplaceSourceAsync(source, """[{"sku":"coffee","name":"New coffee","priceCents":1500},{"sku":"cocoa","name":"Cocoa","priceCents":1000}]""");
        await EventuallyAsync(async () => state.Current.LastError is null && (await sample.Client.GetFromJsonAsync<Product>("/catalog/coffee"))!.PriceCents == 1500);
        Assert.Null(state.Current.LastError);
        using var removed = await sample.Client.GetAsync("/catalog/tea");
        Assert.Equal(HttpStatusCode.NotFound, removed.StatusCode);
        Assert.Equal("Cocoa", (await sample.Client.GetFromJsonAsync<Product>("/catalog/cocoa"))!.Name);

        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => sample.Client.GetFromJsonAsync<Product[]>("/catalog")));
        Assert.All(responses, products => Assert.Equal(2, products!.Length));

        // Keep a reader open so replacement doesn't depend on the worker's timing.
        await using var sourceReader = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        await ReplaceSourceAsync(source, "[]");
        await EventuallyAsync(async () => (await sample.Client.GetFromJsonAsync<Product[]>("/catalog"))!.Length == 0);
    }

    [Fact]
    public async Task BackgroundSampleStopsWithoutWaitingForTheNextTimerTick()
    {
        var folder = Directory.CreateTempSubdirectory("serval-catalog-stop-").FullName;
        var source = Path.Combine(folder, "source.json");
        await ReplaceSourceAsync(source, "[]");
        await using var sample = await RunningSample.StartAsync(Sample.BackgroundServices.Program.Build,
            "--ConnectionStrings:Catalog", ConnectionString(Path.Combine(folder, "catalog.db")),
            "--Catalog:SourcePath", source, "--Catalog:RefreshInterval", "01:00:00");
        var worker = sample.Host.Services.GetServices<IHostedService>().OfType<CatalogRefreshService>().Single();

        await sample.StopAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(worker.ExecuteTask!.IsCompleted);
    }

    [Fact]
    public async Task InvalidInitialCatalogPreventsTheHostFromStarting()
    {
        var folder = Directory.CreateTempSubdirectory("serval-catalog-invalid-").FullName;
        var source = Path.Combine(folder, "source.json");
        await ReplaceSourceAsync(source, """[{"sku":"bad sku","name":"Invalid","priceCents":1}]""");
        var host = Sample.BackgroundServices.Program.Build([
            "--environment", "Development", "--Logging:LogLevel:Default", "None",
            "--port", FreePort().ToString(CultureInfo.InvariantCulture),
            "--ConnectionStrings:Catalog", ConnectionString(Path.Combine(folder, "catalog.db")),
            "--Catalog:SourcePath", source,
        ]);
        await using var lifetime = (IAsyncDisposable)host;

        await Assert.ThrowsAsync<InvalidDataException>(() => host.StartAsync());
        Assert.False(host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStarted.IsCancellationRequested);
    }

    [Theory]
    [InlineData("00:00:00")]
    [InlineData("02:00:00")]
    public async Task InvalidRefreshIntervalsFailOptionsValidationAtStartup(string interval)
    {
        var host = Sample.BackgroundServices.Program.Build([
            "--environment", "Development", "--Logging:LogLevel:Default", "None",
            "--port", FreePort().ToString(CultureInfo.InvariantCulture),
            "--Catalog:RefreshInterval", interval,
        ]);
        await using var lifetime = (IAsyncDisposable)host;
        await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
    }

    private static string ConnectionString(string path) => new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Pooling    = false
    }.ToString();

    private static async Task ReplaceSourceAsync(string path, string content)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(temporary, content);
        if (File.Exists(path))
        {
            // Move with overwrite can fail on Windows while the worker has the file open.
            File.Replace(temporary, path, null);
        }
        else
        {
            File.Move(temporary, path);
        }
    }

    private static async Task EventuallyAsync(Func<Task<bool>> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!await condition())
        {
            await Task.Delay(25, timeout.Token);
        }
    }

    private static int FreePort()
    {
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        return ((IPEndPoint)reservation.LocalEndpoint).Port;
    }

    private sealed class RunningSample(IHost host, HttpClient client) : IAsyncDisposable
    {
        public  IHost      Host   { get; } = host;
        public  HttpClient Client { get; } = client;
        private bool       _stopped;

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            try
            {
                await StopAsync();
            }
            finally
            {
                await ((IAsyncDisposable)Host).DisposeAsync();
            }
        }

        public async Task StopAsync()
        {
            if (_stopped)
            {
                return;
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await Host.StopAsync(timeout.Token);
            _stopped = true;
        }

        public static async Task<RunningSample> StartAsync(Func<string[], IHost> build, params string[] arguments)
        {
            var port = FreePort();
            var host = build(["--environment", "Development", "--Logging:LogLevel:Default", "None", "--port", port.ToString(CultureInfo.InvariantCulture), .. arguments]);
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await host.StartAsync(timeout.Token);
                return new RunningSample(host, new HttpClient(new SocketsHttpHandler
                {
                    UseProxy = false
                })
                {
                    BaseAddress = new Uri($"http://localhost:{port}"),
                    Timeout     = TimeSpan.FromSeconds(5),
                });
            }
            catch
            {
                await ((IAsyncDisposable)host).DisposeAsync();
                throw;
            }
        }
    }
}
