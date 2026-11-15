#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using Serval.Hosting;
using Serval.Http;

namespace Serval.Transport.Http2;

internal sealed class Http2Server : BackgroundService, IAsyncDisposable
{
    private          long                             _connectionId;
    private readonly ConcurrentDictionary<long, Task> _connections;
    private readonly IRequestHandler                  _handler;
    private readonly Http2Options                     _http2Options;
    private readonly TcpListener                      _listener;
    private readonly ILogger<Http2Server>             _logger;
    private readonly ServalOptions                    _options;
    // Share the request budget across connections; the advertised stream limit alone is only per connection.
    private readonly SemaphoreSlim                    _requestSlots;

    public Http2Server(Http2Options http2Options, ServalOptions options, IRequestHandler handler, ILogger<Http2Server> logger)
    {
        _http2Options = http2Options;
        _options      = options;
        _handler      = handler;
        _logger       = logger;
        _connections  = new ConcurrentDictionary<long, Task>();
        _listener     = new TcpListener(http2Options.EndPoint);
        _requestSlots = new SemaphoreSlim(options.MaxConcurrentRequests);
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        _http2Options.Validate();
        _listener.Start();
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("Serval HTTP/2 listening on {EndPoint}; TLS: {UseTls}.", _listener.LocalEndpoint, _http2Options.UseTls);
        }

        await base.StartAsync(cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await base.StopAsync(cancellationToken);
        }
        finally
        {
            _listener.Stop();
        }

        await Task.WhenAll(_connections.Values).WaitAsync(cancellationToken);
    }

    public override void Dispose()
    {
        base.Dispose();
        _listener.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None);

        _listener.Dispose();
        _requestSlots.Dispose();
        base.Dispose();
    }

    private async Task ProcessConnectionAsync(TcpClient client, long id, TaskCompletionSource completion, CancellationToken stoppingToken)
    {
        using (client)
        {
            try
            {
                await using var network = client.GetStream();
                if (_http2Options.UseTls)
                {
                    await using var tls       = new SslStream(network, true);
                    using var       handshake = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    handshake.CancelAfter(_http2Options.HandshakeTimeout);
                    await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                    {
                        ServerCertificate    = _http2Options.Certificate,
                        ApplicationProtocols = [SslApplicationProtocol.Http2],
                        EnabledSslProtocols  = SslProtocols.Tls12 | SslProtocols.Tls13,
                    }, handshake.Token);

                    if (tls.NegotiatedApplicationProtocol != SslApplicationProtocol.Http2)
                    {
                        return;
                    }

                    await new Http2Connection(tls, _http2Options, _options, _handler, _requestSlots, _logger, stoppingToken,
                        new RequestConnection(client.Client.RemoteEndPoint as IPEndPoint, client.Client.LocalEndPoint as IPEndPoint)).RunAsync();
                }
                else
                {
                    await new Http2Connection(network, _http2Options, _options, _handler, _requestSlots, _logger, stoppingToken,
                        new RequestConnection(client.Client.RemoteEndPoint as IPEndPoint, client.Client.LocalEndPoint as IPEndPoint)).RunAsync();
                }
            }
            catch (Exception exception)
            {
                _logger.LogDebug(exception, "HTTP/2 connection ended.");
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
                var client = await _listener.AcceptTcpClientAsync(stoppingToken);
                if (_connections.Count >= _http2Options.MaxConcurrentConnections)
                {
                    client.Dispose();
                    continue;
                }

                client.NoDelay = true;
                var id         = Interlocked.Increment(ref _connectionId);
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _connections[id] = completion.Task;
                _                = ProcessConnectionAsync(client, id, completion, stoppingToken);
            }
        }
        catch (Exception exception) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogDebug(exception, "The HTTP/2 listener stopped.");
        }
    }
}
