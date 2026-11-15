#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Globalization;
using System.Text.Json;
using Serval.Tests.Support;

namespace Serval.Tests.Samples;

public sealed class LifecycleSampleTests
{
    [Fact]
    public async Task HandlersInitializeScopedStateAndSetFinalHeaders()
    {
        await using var app = new SampleApplication("Lifecycles", "--Logging:LogLevel:Default", "Information");
        var logs = SampleLogProvider.Capture(app, "Lifecycles");
        var response = await app.SendAsync("/work");
        using var json = JsonDocument.Parse(response.Text);
        Assert.Equal(json.RootElement.GetProperty("requestId").GetString(), response.Headers["X-Request-Id"]);
        Assert.Equal(json.RootElement.GetProperty("traceId").GetString(), response.Headers["X-Trace-Id"]);
        Assert.Equal("nosniff", response.Headers["X-Content-Type-Options"]);
        Assert.True(double.Parse(response.Headers["Server-Timing"]!.Split('=')[1], CultureInfo.InvariantCulture) >= 0);
        logs.AssertEvents("timing received", "timing starting (200)", "headers starting (200)", "timing completed", "audit completed");
        Assert.Contains(response.Headers["X-Request-Id"]!, logs.Messages.Last());
        Assert.NotEqual(response.Headers["X-Request-Id"], (await app.SendAsync("/work")).Headers["X-Request-Id"]);
    }

    [Theory]
    [InlineData("/crash", 500, false, "Completed")]
    [InlineData("/missing", 404, false, "Completed")]
    [InlineData("/lifecycle.json", 200, false, "Completed")]
    [InlineData("/work/", 308, false, "Completed")]
    [InlineData("/partial", 200, true, "Aborted")]
    public async Task LifecycleSampleCoversFilesRedirectsErrorsAndAborts(string path, int status, bool aborted, string reason)
    {
        await using var app = new SampleApplication("Lifecycles", "--Logging:LogLevel:Default", "Information");
        var logs = SampleLogProvider.Capture(app, "Lifecycles");
        var response = await app.SendAsync(path);
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(aborted, response.Aborted);
        Assert.NotNull(response.Headers["X-Request-Id"]);
        Assert.Equal("nosniff", response.Headers["X-Content-Type-Options"]);
        Assert.Contains($"status {status}, reason {reason}", logs.Messages.Last());
        if (path == "/lifecycle.json")
        {
            Assert.Contains("Lifecycle handlers also cover static files", response.Text);
        }
    }

    [Fact]
    public async Task LocalCallbacksRunBeforeTheCorrespondingLifecycleHooks()
    {
        await using var app = new SampleApplication("Lifecycles", "--Logging:LogLevel:Default", "Information");
        var logs = SampleLogProvider.Capture(app, "Lifecycles");
        var response = await app.SendAsync("/callback");
        Assert.Equal("callback", response.Headers["X-Action"]);
        logs.AssertEvents("timing received", "action starting", "timing starting", "headers starting",
            "action completed", "timing completed", "audit completed");
    }

    [Fact]
    public async Task DeadlineProducesA408WithCompletionReasonAndFinalHeaders()
    {
        await using var app = new SampleApplication("Lifecycles", "--timeout-ms", "40", "--Logging:LogLevel:Default", "Information");
        var logs = SampleLogProvider.Capture(app, "Lifecycles");
        var response = await app.SendAsync("/slow");
        Assert.Equal(408, response.StatusCode);
        Assert.Equal("nosniff", response.Headers["X-Content-Type-Options"]);
        Assert.Contains("reason TimedOut", logs.Messages.Last());
    }
}
