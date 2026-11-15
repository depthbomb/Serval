#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Net.Quic;
using System.Net.Security;
using System.Runtime.Versioning;
using System.Security.Authentication;
using Serval.Hosting;
using Serval.Transport.Http3.Protocol;

namespace Serval.Transport.Http3;

[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
internal sealed class Http3Server : BackgroundService, IAsyncDisposable
{
    private          long                             _connectionId;
    private          QuicListener?                    _listener;
    private readonly ConcurrentDictionary<long, Task> _connections;
    private readonly IRequestHandler                  _handler;
    private readonly Http3Options                     _http3Options;
    private readonly ILogger<Http3Server>             _logger;
    private readonly ServalOptions                    _options;
    // QUIC's stream limit is per connection; this semaphore caps application work across the listener.
    private readonly SemaphoreSlim                    _requestSlots;


    public Http3Server(Http3Options http3Options, ServalOptions options, IRequestHandler handler, ILogger<Http3Server> logger)
    {
        _http3Options = http3Options;
        _options      = options;
        _handler      = handler;
        _logger       = logger;
        _connections  = new ConcurrentDictionary<long, Task>();
        _requestSlots = new SemaphoreSlim(options.MaxConcurrentRequests);
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        _listener = await QuicListener.ListenAsync(new QuicListenerOptions
        {
            ListenEndPoint       = _http3Options.EndPoint,
            ApplicationProtocols = [new SslApplicationProtocol("h3")],
            ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(new QuicServerConnectionOptions
            {
                DefaultCloseErrorCode           = Http3Error.NoError,
                DefaultStreamErrorCode          = Http3Error.RequestCancelled,
                MaxInboundBidirectionalStreams  = _options.MaxConcurrentRequests,
                MaxInboundUnidirectionalStreams = 16,
                IdleTimeout                     = _http3Options.IdleTimeout,
                ServerAuthenticationOptions = new SslServerAuthenticationOptions
                {
                    ApplicationProtocols = [new SslApplicationProtocol("h3")],
                    ServerCertificate    = _http3Options.Certificate,
                    EnabledSslProtocols  = SslProtocols.Tls13,
                },
            }),
        }, cancellationToken);

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("Serval HTTP/3 listening on {EndPoint}.", _listener.LocalEndPoint);
        }

        await base.StartAsync(cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        if (_listener is not null)
        {
            await _listener.DisposeAsync();
            _listener = null;
        }

        await Task.WhenAll(_connections.Values).WaitAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None);
        _requestSlots.Dispose();
        Dispose();
    }

    private async Task ProcessConnectionAsync(QuicConnection       connection,
                                              long                 id,
                                              TaskCompletionSource completion,
                                              CancellationToken    cancellationToken)
    {
        await using (connection)
        {
            try
            {
                await new Http3Connection(connection, _options, _handler, _requestSlots, _logger).RunAsync(cancellationToken);
            }
            catch (Exception exception)
            {
                _logger.LogDebug(exception, "HTTP/3 connection ended.");
            }
            finally
            {
                _connections.TryRemove(id, out _);
                completion.TrySetResult();
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                QuicConnection connection;
                try
                {
                    connection = await _listener!.AcceptConnectionAsync(stoppingToken);
                }
                catch (Exception exception) when (!stoppingToken.IsCancellationRequested && exception is AuthenticationException or QuicException { QuicError: QuicError.ConnectionAborted or QuicError.ConnectionTimeout or QuicError.TransportError })
                {
                    _logger.LogDebug(exception, "HTTP/3 handshake failed.");
                    continue;
                }

                if (_connections.Count >= _http3Options.MaxConcurrentConnections)
                {
                    await connection.CloseAsync(Http3Error.ExcessiveLoad, stoppingToken);
                    await connection.DisposeAsync();
                    continue;
                }

                var id         = Interlocked.Increment(ref _connectionId);
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _connections[id] = completion.Task;
                _                = ProcessConnectionAsync(connection, id, completion, stoppingToken);
            }
        }
        catch (OperationCanceledException exception) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogDebug(exception, "The HTTP/3 listener stopped.");
        }
    }
}
