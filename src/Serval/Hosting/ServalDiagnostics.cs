#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Diagnostics;
using System.Diagnostics.Metrics;
using Serval.Http;
using Serval.Http.Constants;
using Serval.Validation;

namespace Serval.Hosting;

/// <summary>Identifies the framework's request tracing and metrics instruments.</summary>
public static class ServalDiagnostics
{
    /// <summary>The ActivitySource and Meter name to subscribe to with diagnostic listeners or telemetry exporters.</summary>
    public const string InstrumentationName = "Serval";

    private static readonly ActivitySource      Activities     = new(InstrumentationName);
    private static readonly Meter               Metrics        = new(InstrumentationName);
    private static readonly UpDownCounter<long> ActiveRequests = Metrics.CreateUpDownCounter<long>("http.server.active_requests", "{request}");
    private static readonly Histogram<double>   Duration       = Metrics.CreateHistogram<double>("http.server.request.duration", "s");
    private static readonly Counter<long>       Errors         = Metrics.CreateCounter<long>("http.server.request.errors", "{request}");

    internal static Activity? Start(Request request)
    {
        var method = Method(request.Method);
        ActiveRequests.Add(1, new KeyValuePair<string, object?>("http.request.method", method));
        var activity = ActivityContext.TryParse(request.Headers[HeaderNames.TraceParent], request.Headers[HeaderNames.TraceState], true, out var parent)
            ? Activities.StartActivity("HTTP " + method, ActivityKind.Server, parent)
            : Activities.StartActivity("HTTP " + method, ActivityKind.Server);
        activity?.SetTag("http.request.method", method);
        return activity;
    }

    internal static void Complete(Request request, Response response, RequestContext? context, Activity? activity, long startedAt, Exception? failure)
    {
        try
        {
            Record(request, response, context, activity, startedAt, failure);
        }
        finally
        {
            ActiveRequests.Add(-1, new KeyValuePair<string, object?>("http.request.method", Method(request.Method)));
        }
    }

    private static void Record(Request request, Response response, RequestContext? context, Activity? activity, long startedAt, Exception? failure)
    {
        var method = Method(request.Method);
        var route  = context?.Endpoint?.Template;
        var error  = response.StatusCode >= 500 || failure is not null && failure is not HttpErrorException && failure is not InputValidationException;
        var tags = new TagList
        {
            { "http.request.method", method },
            { "http.response.status_code", response.StatusCode },
        };
        if (route is not null)
        {
            tags.Add("http.route", route);
            activity?.SetTag("http.route", route);
            activity?.DisplayName = method + " " + route;
        }

        activity?.SetTag("url.scheme", request.Scheme);
        activity?.SetTag("http.response.status_code", response.StatusCode);
        if (error)
        {
            var type = failure?.GetType().FullName ?? "http.server.error";
            tags.Add("error.type", type);
            activity?.SetTag("error.type", type);
            activity?.SetStatus(ActivityStatusCode.Error);
            Errors.Add(1, tags);
        }

        Duration.Record(Stopwatch.GetElapsedTime(startedAt).TotalSeconds, tags);
    }

    private static string Method(string method)
        => method is HttpMethods.Get or HttpMethods.Query or HttpMethods.Head or HttpMethods.Post or HttpMethods.Put or HttpMethods.Delete or HttpMethods.Connect or HttpMethods.Options or HttpMethods.Trace or HttpMethods.Patch ? method : "_OTHER";
}
