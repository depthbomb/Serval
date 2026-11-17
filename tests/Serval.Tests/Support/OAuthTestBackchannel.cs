#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace Serval.Tests.Support;

internal sealed record OAuthTestRequest(Uri Url, string Method, string? Authorization, string Body, bool CustomHeader);

internal sealed class OAuthTestBackchannel : HttpMessageHandler
{
    public ConcurrentQueue<OAuthTestRequest>            Requests { get; } = new();
    public Func<OAuthTestRequest, HttpResponseMessage>? Respond  { get; set; }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var captured = new OAuthTestRequest(request.RequestUri!, request.Method.Method, request.Headers.Authorization?.ToString(),
            request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken), request.Headers.Contains("X-Custom"));
        Requests.Enqueue(captured);
        return Respond?.Invoke(captured) ?? Json(captured.Url.AbsolutePath == "/profile"
            ? "{\"id\":\"provider-user\",\"name\":\"Ada\"}"
            : "{\"access_token\":\"access-secret\",\"token_type\":\"Bearer\",\"refresh_token\":\"refresh-secret\",\"expires_in\":3600}");
    }
}
