#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;
using System.Text.Json;
using Serval.Hosting;
using Serval.Http.Constants;
using Serval.Http.Results;
using Serval.Tests.Support;

namespace Serval.Tests.Core.Hosting;

public sealed class TraceIdTests
{
    [Fact]
    public async Task FactoriesAreIsolatedPerApplicationAndRunOncePerRequest()
    {
        using var       activity    = new Activity("custom-trace-ids").SetIdFormat(ActivityIdFormat.W3C).Start();
        var             activityId  = activity.TraceId;
        var             firstCalls  = 0;
        var             secondCalls = 0;
        await using var first       = Application(options => options.TraceIdGenerator = () => "first-"  + Interlocked.Increment(ref firstCalls));
        await using var second      = Application(options => options.TraceIdGenerator = () => "second-" + Interlocked.Increment(ref secondCalls));
        _ = first.Services.GetRequiredService<IRequestHandler>();
        _ = second.Services.GetRequiredService<IRequestHandler>();
        Assert.Equal(0, firstCalls);
        Assert.Equal(0, secondCalls);

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).SelectMany(_ => new[] { first.SendAsync("/trace"), second.SendAsync("/trace") }));
        Assert.All(responses, response => Assert.Equal(StatusCodes.Ok, response.StatusCode));
        Assert.Equal(Enumerable.Range(1, 8).Select(index => "first-"  + index), responses.Where((_, index) => index % 2 == 0).Select(response => response.Text).Order());
        Assert.Equal(Enumerable.Range(1, 8).Select(index => "second-" + index), responses.Where((_, index) => index % 2 != 0).Select(response => response.Text).Order());
        Assert.Equal(8, firstCalls);
        Assert.Equal(8, secondCalls);

        var missing = await first.SendAsync("/missing");
        Assert.Equal(StatusCodes.NotFound, missing.StatusCode);
        using var problem = JsonDocument.Parse(missing.Text);
        Assert.Equal("first-9", problem.RootElement.GetProperty("traceId").GetString());
        Assert.Equal(9, firstCalls);
        Assert.Same(activity, Activity.Current);
        Assert.Equal(activityId, activity.TraceId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DefaultFactoryUsesTheActivityOrCreatesAnIdForEachRequest(bool withActivity)
    {
        var previous = Activity.Current;
        Activity.Current = null;
        try
        {
            using var       activity = withActivity ? new Activity("default-trace-ids").SetIdFormat(ActivityIdFormat.W3C).Start() : null;
            await using var app      = Application();
            var             first    = await app.SendAsync("/trace");
            var             second   = await app.SendAsync("/trace");
            Assert.Equal(StatusCodes.Ok, first.StatusCode);
            Assert.Equal(StatusCodes.Ok, second.StatusCode);
            if (activity is not null)
            {
                Assert.Equal(activity.TraceId.ToString(), first.Text);
                Assert.Equal(first.Text, second.Text);
            }
            else
            {
                Assert.True(Guid.TryParseExact(first.Text, "N", out _));
                Assert.True(Guid.TryParseExact(second.Text, "N", out _));
                Assert.NotEqual(first.Text, second.Text);
            }
        }
        finally
        {
            Activity.Current = previous;
        }
    }

    [Theory]
    [InlineData("throws")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n")]
    public async Task BrokenFactoriesUseTheErrorHandlerWithADefaultIdWithoutRetrying(string? result)
    {
        using var activity = new Activity("failed-trace-generator").SetIdFormat(ActivityIdFormat.W3C).Start();
        var       calls    = 0;
        await using var app = Application(options => options.TraceIdGenerator = () =>
        {
            calls++;
            return result == "throws" ? throw new InvalidOperationException("generator failure") : result!;
        });

        var response = await app.SendAsync("/trace");
        Assert.Equal(StatusCodes.InternalServerError, response.StatusCode);
        Assert.False(response.Aborted);
        Assert.Equal(1, calls);
        using var problem = JsonDocument.Parse(response.Text);
        Assert.Equal(activity.TraceId.ToString(), problem.RootElement.GetProperty("traceId").GetString());
        Assert.Equal(JsonValueKind.Null, problem.RootElement.GetProperty("detail").ValueKind);
    }

    [Fact]
    public async Task NullFactoryIsRejectedWhenTheApplicationIsValidated()
    {
        await using var app   = Application(options => options.TraceIdGenerator = null!);
        var             error = Assert.Throws<ArgumentNullException>(() => app.Services.GetRequiredService<IRequestHandler>());
        Assert.Equal(nameof(ServalOptions.TraceIdGenerator), error.ParamName);
    }

    private static TestApplication Application(Action<ServalOptions>? configure = null)
        => new(configure, configureBuilder: builder => builder.AddController<object>(HttpMethods.Get, "/trace", null, async (_, context) =>
        {
            var traceId = context.TraceId;
            await Task.Yield();
            Assert.Equal(traceId, context.TraceId);
            return new TextResult(context.TraceId);
        }));
}
