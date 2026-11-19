using System.Diagnostics;
using Xunit.Abstractions;

namespace Serval.SocketIO.Tests;

public sealed class JavaScriptClientTests
{
    private readonly ITestOutputHelper _output;

    public JavaScriptClientTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task OfficialClientInteroperatesOverAllTransports()
    {
        await using var server    = await TestServer.StartAsync();
        var             directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Serval.SocketIO.Tests.csproj")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        Assert.True(File.Exists(Path.Combine(directory.FullName, "node_modules", "socket.io-client", "package.json")),
            "Install the pinned official client first: npm ci --prefix tests/Serval.SocketIO.Tests --ignore-scripts");
        var start = new ProcessStartInfo("node")
        {
            WorkingDirectory       = directory.FullName,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };
        start.ArgumentList.Add("--test");
        start.ArgumentList.Add("--test-reporter=tap");
        start.ArgumentList.Add("client.test.mjs");
        start.Environment["SERVAL_SOCKETIO_URL"] = server.Url;
        using var process = Process.Start(start)!;
        var       stdout  = process.StandardOutput.ReadToEndAsync();
        var       stderr  = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }

        _output.WriteLine(await stdout);
        _output.WriteLine(await stderr);
        Assert.Equal(0, process.ExitCode);
        await server.Host.StopAsync(timeout.Token);
        Assert.Equal(0, server.Server.ConnectionCount);
        Assert.All(server.Scopes.Scopes.Values, Assert.True);
        Assert.NotEmpty(server.Scopes.Disconnected);
    }
}
