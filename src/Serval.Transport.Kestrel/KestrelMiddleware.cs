#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Net;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.Extensions.Hosting;
using Serval.Hosting;
using Serval.Http;
using StatusCodes = Serval.Http.Constants.StatusCodes;

namespace Serval.Transport.Kestrel;

internal sealed class KestrelMiddleware
{
    private int _activeRequests;

    private readonly IRequestHandler   _handler;
    private readonly ServalOptions     _options;
    private readonly CancellationToken _stopping;

    public KestrelMiddleware(IRequestHandler handler, ServalOptions options, IHostApplicationLifetime lifetime)
    {
        _handler  = handler;
        _options  = options;
        _stopping = lifetime.ApplicationStopping;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var active = Interlocked.Increment(ref _activeRequests);
        try
        {
            if (active > _options.MaxConcurrentRequests || _stopping.IsCancellationRequested)
            {
                context.Response.StatusCode    = StatusCodes.ServiceUnavailable;
                context.Response.ContentLength = 0;
                return;
            }

            var nativeRequest = context.Request;
            var connection = context.Connection;
            // Request-level connection fields can be rewritten by upstream middleware.
            var endpoints = context.Features.Get<IConnectionEndPointFeature>();
            var localEndPoint = endpoints?.LocalEndPoint as IPEndPoint ??
                (connection.LocalIpAddress is {} local ? new IPEndPoint(local, connection.LocalPort) : null);
            var host = nativeRequest.Host;
            if (!host.HasValue && nativeRequest.Protocol == "HTTP/1.0")
            {
                // Host is optional in HTTP/1.0. Use the accepting endpoint when the client supplies no authority.
                host = localEndPoint is not null
                    ? new HostString(localEndPoint.Address.ToString(), localEndPoint.Port)
                    : new HostString("localhost");
            }

            var encodedUrl = UriHelper.BuildAbsolute(nativeRequest.Scheme, host, nativeRequest.PathBase, nativeRequest.Path, nativeRequest.QueryString);
            if (!Uri.TryCreate(encodedUrl, UriKind.Absolute, out var url))
            {
                context.Response.StatusCode = StatusCodes.BadRequest;
                return;
            }

            var headers = new Headers(context.Request.Headers.Count);
            foreach (var (name, values) in context.Request.Headers)
            {
                if (values.Count == 1)
                {
                    headers[name] = values[0] ?? string.Empty;
                }
                else
                {
                    foreach (var value in values)
                    {
                        headers.Append(name, value ?? string.Empty);
                    }
                }
            }

            var version = context.Request.Protocol switch
            {
                "HTTP/1.0" => HttpVersion.Version10,
                "HTTP/1.1" => HttpVersion.Version11,
                "HTTP/2"   => HttpVersion.Version20,
                "HTTP/3"   => HttpVersion.Version30,
                _          => throw new RequestTransportException("Unsupported HTTP version.")
            };
            var remoteEndPoint = endpoints?.RemoteEndPoint as IPEndPoint ??
                (connection.RemoteIpAddress is {} remote ? new IPEndPoint(remote, connection.RemotePort) : null);
            var request = new Request(context.Request.Method, url, version, headers,
                new KestrelRequestBodyStream(context.Request.Body), _options.MaxRequestBodyBytes,
                new RequestConnection(remoteEndPoint, localEndPoint));
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, _stopping);
            await _handler.HandleAsync(request, new KestrelResponse(context), cancellation.Token);
        }
        catch (RequestTransportException)
        {
            context.Abort();
        }
        finally
        {
            Interlocked.Decrement(ref _activeRequests);
        }
    }
}
