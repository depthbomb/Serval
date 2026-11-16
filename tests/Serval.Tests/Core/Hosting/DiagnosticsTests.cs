using Microsoft.Extensions.DependencyInjection;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Serval.Hosting;
using Serval.Http;
using Serval.Http.Constants;
using Serval.Http.Results;
using Serval.Tests.Support;

namespace Serval.Tests.Core.Hosting;

public sealed class DiagnosticsTests
{
    [Fact]
    public async Task BrokenMetricListenersCannotSkipCompletionOrScopeDisposal()
    {
        ScopedProbe? probe     = null;
        var          completed = false;
        using var    meter     = new MeterListener();
        meter.InstrumentPublished = (instrument, owner) =>
        {
            if (instrument.Meter.Name == ServalDiagnostics.InstrumentationName && instrument.Name == "http.server.request.duration")
            {
                owner.EnableMeasurementEvents(instrument);
            }
        };
        meter.SetMeasurementEventCallback<double>((_, _, tags, _) =>
        {
            if (tags.ToArray().Any(tag => tag is { Key: "http.route", Value: "/broken-metrics" }))
            {
                throw new InvalidOperationException("A telemetry exporter failed.");
            }
        });
        meter.Start();
        await using var app = new TestApplication(configureBuilder: builder => builder.AddController<object>("GET", "/broken-metrics", null, (_, context) =>
        {
            probe = context.Services.GetRequiredService<ScopedProbe>();
            context.Response.OnCompleted(() =>
            {
                completed = true;
                return ValueTask.CompletedTask;
            });
            return ValueTask.FromResult<IActionResult>(new TextResult("ok"));
        }));
        Assert.Equal("ok", (await app.SendAsync("/broken-metrics")).Text);
        Assert.True(completed);
        Assert.True(probe!.Disposed);
    }

    [Theory]
    [InlineData(HttpMethods.Get)]
    [InlineData(HttpMethods.Query)]
    public async Task TracingPropagatesParentsAndMetricsUseTemplatesWithoutSensitiveValues(string method)
    {
        var trace        = ActivityTraceId.CreateRandom();
        var span         = ActivitySpanId.CreateRandom();
        var activities   = new ConcurrentBag<Activity>();
        var measurements = new ConcurrentBag<(double Value, Dictionary<string, object?> Tags)>();
        using var listener = new ActivityListener();
        listener.ShouldListenTo  = source => source.Name == ServalDiagnostics.InstrumentationName;
        listener.Sample          = (ref options) => options.Parent.TraceId == trace ? ActivitySamplingResult.AllDataAndRecorded : ActivitySamplingResult.None;
        listener.ActivityStopped = activities.Add;
        ActivitySource.AddActivityListener(listener);
        using var meter = new MeterListener();
        meter.InstrumentPublished = (instrument, owner) =>
        {
            if (instrument.Meter.Name == ServalDiagnostics.InstrumentationName && instrument.Name == "http.server.request.duration")
            {
                owner.EnableMeasurementEvents(instrument);
            }
        };
        meter.SetMeasurementEventCallback<double>((_, value, tags, _) =>
        {
            var values = tags.ToArray().ToDictionary(pair => pair.Key, pair => pair.Value);
            if (values.GetValueOrDefault("http.route") as string == "/diagnostics/{id}")
            {
                measurements.Add((value, values));
            }
        });
        meter.Start();
        await using var app = new TestApplication(configureBuilder: builder => builder.AddController<object>(method, "/diagnostics/{id}", null, (_, context) =>
            context.RouteValues["id"] == "failure"
                ? throw new InvalidOperationException("private exception detail")
                : ValueTask.FromResult<IActionResult>(new TextResult(context.TraceId))));
        var response = await app.SendAsync("/diagnostics/secret?token=secret", method, contentType: MimeTypes.Json, configureHeaders: headers => headers["traceparent"] = $"00-{trace}-{span}-01");
        Assert.Equal(trace.ToString(), response.Text);
        Assert.Equal(500, (await app.SendAsync("/diagnostics/failure", method, contentType: MimeTypes.Json, configureHeaders: headers => headers["traceparent"] = $"00-{trace}-{span}-01")).StatusCode);
        Assert.Equal(2, activities.Count);
        Assert.All(activities, activity =>
        {
            Assert.Equal(span, activity.ParentSpanId);
            Assert.Equal(ActivityKind.Server, activity.Kind);
            Assert.Equal(method + " /diagnostics/{id}", activity.DisplayName);
            Assert.Equal(method, activity.GetTagItem("http.request.method"));
            Assert.DoesNotContain(activity.TagObjects, tag => tag.Value?.ToString()?.Contains("secret") == true || tag.Value?.ToString()?.Contains("private") == true);
        });
        Assert.Contains(activities, activity => activity.Status == ActivityStatusCode.Error);
        Assert.Equal(2, measurements.Count);
        Assert.All(measurements, measurement =>
        {
            Assert.True(measurement.Value >= 0);
            Assert.Equal(method, measurement.Tags["http.request.method"]);
        });
        Assert.Contains(measurements, measurement => Equals(measurement.Tags["http.response.status_code"], 500));
    }
}
