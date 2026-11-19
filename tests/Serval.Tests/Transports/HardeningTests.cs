#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.Extensions.DependencyInjection;
using System.Net;
using Serval.Hosting;
using Serval.Http;
using Serval.Http.Constants;
using Serval.Http.Results;
using Serval.Routing;
using Serval.Tests.Support;

namespace Serval.Tests.Transports;

public sealed class HardeningTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequestDeadlinesApplyToBodyReadsWithoutTheContextToken(bool supplyIndependentToken)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddServal(options => options.RequestTimeout = TimeSpan.FromMilliseconds(60)).AddController<object>("POST", "/", null, async (_, context) =>
        {
            using var independent = new CancellationTokenSource();
            await context.Request.ReadBodyAsync(supplyIndependentToken ? independent.Token : CancellationToken.None);
            return EmptyResult.Instance;
        });
        await using var provider = services.BuildServiceProvider();
        await using var body     = new StalledReadStream();
        var             request  = new Request("POST", new Uri("http://localhost/"), HttpVersion.Version11, new Headers(), body, 1024);
        var             response = new MemoryResponse();
        await provider.GetRequiredService<IRequestHandler>().HandleAsync(request, response, CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(StatusCodes.RequestTimeout, response.StatusCode);
    }

    [Fact]
    public async Task ConflictingRequestFramingIsRejectedBeforeDispatch()
    {
        await using var app = new TestApplication();
        var response = await app.SendAsync("/test/task", configureHeaders: headers =>
        {
            headers[HeaderNames.ContentLength] = "0";
            headers["Transfer-Encoding"]       = "chunked";
        });
        Assert.Equal(StatusCodes.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData(204, null)]
    [InlineData(200, "chunked")]
    public async Task ContradictoryResponseFramingFailsBeforeCommit(int status, string? transferEncoding)
    {
        var response = new MemoryResponse
        {
            StatusCode = status,
            Headers =
            {
                [HeaderNames.ContentLength] = "0",
                ["Transfer-Encoding"]       = transferEncoding,
            },
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => response.StartAsync().AsTask());
        Assert.False(response.HasStarted);
    }

    [Fact]
    public void EmptySegmentsDoNotBindRequiredParameters()
    {
        var route = new RouteTemplate("/a/{id}/b", new ServalOptions());
        Assert.False(route.TryMatch(["a", "", "b"], false, out _));
        Assert.Throws<ArgumentException>(() => new RouteTemplate("/a//", new ServalOptions()));
        var optional = new RouteTemplate("/a/{id=}", new ServalOptions());
        Assert.True(optional.TryMatch(["a"], false, out var values));
        Assert.Equal("", values["id"]);
    }

    [Fact]
    public async Task CancellationStopsFurtherRouteConstraintEvaluation()
    {
        using var cancellation = new CancellationTokenSource();
        var       calls        = 0;
        var options = new ServalOptions
        {
            RouteConstraints =
            {
                ["probe"] = (_, _) =>
                {
                    calls++;
                    cancellation.Cancel();
                    return false;
                },
            },
        };
        var registry = new ControllerRegistry();
        for (var index = 0; index < 3; index++)
        {
            registry.Add<object>("GET", $"/a/{{id:probe({index})}}", null, (_, _) => ValueTask.FromResult<IActionResult>(EmptyResult.Instance));
        }

        var             router   = new Router(registry, options);
        var             request  = new Request("GET", new Uri("http://localhost/a/value"), HttpVersion.Version11, new Headers(), Stream.Null, 1024);
        await using var services = new ServiceCollection().BuildServiceProvider();
        var             context  = new RequestContext(request, new MemoryResponse(), services, router, cancellation.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => router.TryDispatchAsync(context).AsTask());
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Win32PathAliasesCannotEscapeTheStaticRoot()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = Path.Combine(Path.GetTempPath(), "serval-alias-" + Guid.NewGuid().ToString("N"));
        var root      = Path.Combine(directory, "public");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(directory, "secret.txt"), "private");
        await File.WriteAllTextAsync(Path.Combine(root, "safe.txt"), "public");
        await using var app = new TestApplication(options =>
        {
            options.StaticFiles.DirectoryPath = root;
            options.StaticFiles.ServeDotFiles = true;
        });
        Assert.Equal(StatusCodes.NotFound, (await app.SendAsync("/..%20/secret.txt")).StatusCode);
        Assert.Equal(StatusCodes.NotFound, (await app.SendAsync("/safe.txt%20")).StatusCode);
        Assert.Equal("public", (await app.SendAsync("/safe.txt")).Text);
    }

    private sealed class StalledReadStream : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }
}
