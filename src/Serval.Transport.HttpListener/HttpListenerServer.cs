#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Net;
using Serval.Hosting;
using Serval.Http;
using Serval.Http.Constants;

namespace Serval.Transport.HttpListener;

internal sealed class HttpListenerServer : BackgroundService, IAsyncDisposable
{
    private          long                             _requestId;
    private readonly IRequestHandler                  _handler;
    private readonly System.Net.HttpListener          _listener = new();
    private readonly HttpListenerOptions              _listenerOptions;
    private readonly ILogger<HttpListenerServer>      _logger;
    private readonly ServalOptions                    _options;
    private readonly ConcurrentDictionary<long, Task> _requests = new();

    public HttpListenerServer(HttpListenerOptions         listenerOptions,
                              ServalOptions               options,
                              IRequestHandler             handler,
                              ILogger<HttpListenerServer> logger)
    {
        _listenerOptions = listenerOptions;
        _options         = options;
        _handler         = handler;
        _logger          = logger;
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var prefix in _listenerOptions.Prefixes)
        {
            _listener.Prefixes.Add(prefix);
        }

        _listener.Start();
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("Serval HTTP listener started on {Prefixes}.", string.Join(", ", _listenerOptions.Prefixes));
        }

        await base.StartAsync(cancellationToken);
    }

    public override async Task StopAsync(CancellationToken ct)
    {
        // Cancel the loop before its registration stops the listener. Unix can report an interrupted accept as disposal.
        await base.StopAsync(ct);
        await Task.WhenAll(_requests.Values).WaitAsync(ct);
    }

    public override void Dispose()
    {
        base.Dispose();
        _listener.Close();
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await StopAsync(CancellationToken.None);
        }
        finally
        {
            Dispose();
        }
    }

    private async Task ProcessAsync(HttpListenerContext context, long id, TaskCompletionSource completion, CancellationToken ct)
    {
        try
        {
            var headers = new Headers();
            foreach (var key in context.Request.Headers.AllKeys)
            {
                if (key is null)
                {
                    continue;
                }

                foreach (var value in context.Request.Headers.GetValues(key) ?? [])
                {
                    headers.Append(key, value);
                }
            }

            var request = new Request(context.Request.HttpMethod, context.Request.Url!, context.Request.ProtocolVersion, headers, context.Request.InputStream, _options.MaxRequestBodyBytes,
                new RequestConnection(context.Request.RemoteEndPoint, context.Request.LocalEndPoint));
            await _handler.HandleAsync(request, new HttpListenerResponse(context), ct);
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "HTTP connection ended before the request completed.");
            context.Response.Abort();
        }
        finally
        {
            _requests.TryRemove(id, out _);
            completion.TrySetResult();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // A pre-canceled registration runs immediately, possibly after Dispose has already closed the listener.
            await using var registration = stoppingToken.Register(_listener.Stop);
            while (!stoppingToken.IsCancellationRequested)
            {
                var context = await _listener.GetContextAsync();
                if (_requests.Count >= _options.MaxConcurrentRequests)
                {
                    context.Response.StatusCode = StatusCodes.ServiceUnavailable;
                    context.Response.Close();
                    continue;
                }

                var id         = Interlocked.Increment(ref _requestId);
                // Register completion before starting work; an immediately completed handler must still be tracked correctly.
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _requests[id] = completion.Task;
                _             = ProcessAsync(context, id, completion, stoppingToken);
            }
        }
        catch (HttpListenerException exception) when (!_listener.IsListening || stoppingToken.IsCancellationRequested)
        {
            _logger.LogDebug(exception, "The HTTP listener stopped.");
        }
        catch (ObjectDisposedException exception) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogDebug(exception, "The HTTP listener was disposed during shutdown.");
        }
        catch (InvalidOperationException exception) when (stoppingToken.IsCancellationRequested)
        {
            // Cancellation can stop the listener between the loop check and GetContextAsync.
            _logger.LogDebug(exception, "The HTTP listener stopped before accepting another request.");
        }
        catch (ArgumentException exception) when (stoppingToken.IsCancellationRequested && exception.ParamName == "handle")
        {
            // Windows may close the request queue while GetContextAsync binds its handle.
            _logger.LogDebug(exception, "The HTTP listener request queue closed during shutdown.");
        }
    }
}
