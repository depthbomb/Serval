#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Text.Json;
using Serval.Tests.Support;

namespace Serval.Tests.Samples;

public sealed class MiddlewareSampleTests
{
    [Fact]
    public async Task MiddlewareAndControllerShareARequestScope()
    {
        await using var app = new SampleApplication("Middleware", "--Logging:LogLevel:Default", "Information");
        var logs = SampleLogProvider.Capture(app, "Middleware");
        var first = await app.SendAsync("/work");
        using var json = JsonDocument.Parse(first.Text);
        var id = json.RootElement.GetProperty("requestId").GetString()!;
        Assert.Equal(200, first.StatusCode);
        Assert.Contains(id, logs.Messages.First());
        Assert.All(logs.Messages, message => Assert.StartsWith(json.RootElement.GetProperty("traceId").GetString() + ":", message));

        using var second = JsonDocument.Parse((await app.SendAsync("/work")).Text);
        Assert.NotEqual(id, second.RootElement.GetProperty("requestId").GetString());
    }

    [Theory]
    [InlineData("/work", 200)]
    [InlineData("/crash", 500)]
    public async Task MiddlewareWrapsActionsAndUnwindsOnFailure(string path, int status)
    {
        await using var app = new SampleApplication("Middleware", "--Logging:LogLevel:Default", "Information");
        var logs = SampleLogProvider.Capture(app, "Middleware");
        Assert.Equal(status, (await app.SendAsync(path)).StatusCode);
        logs.AssertEvents("application middleware entered.", "route middleware entered.",
            "route middleware exited", "application middleware exited");
    }

    [Fact]
    public async Task MissingRouteRunsOnlyApplicationMiddleware()
    {
        await using var app = new SampleApplication("Middleware", "--Logging:LogLevel:Default", "Information");
        var logs = SampleLogProvider.Capture(app, "Middleware");
        Assert.Equal(404, (await app.SendAsync("/missing")).StatusCode);
        logs.AssertEvents("application middleware entered.", "application middleware exited");
    }

    [Fact]
    public async Task MaintenanceShortCircuitsInnerMiddlewareAndController()
    {
        await using var app = new SampleApplication("Middleware", "--maintenance", "true", "--Logging:LogLevel:Default", "Information");
        var logs = SampleLogProvider.Capture(app, "Middleware");
        var response = await app.SendAsync("/work");
        Assert.Equal(503, response.StatusCode);
        Assert.Equal("60", response.Headers["Retry-After"]);
        Assert.DoesNotContain("The controller ran", response.Text);
        logs.AssertEvents("application middleware entered.", "application middleware exited");
        Assert.Equal(200, (await app.SendAsync("/")).StatusCode);
    }
}
