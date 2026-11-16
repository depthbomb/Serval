#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serval.Http;
using Serval.SocketIO.Internal;
using Serval.Http.Constants;

namespace Serval.SocketIO;

/// <summary>Hosts Socket.IO sessions and provides in-memory namespace and room broadcasts.</summary>
public sealed class SocketIOServer : IHostedService, IDisposable
{
    /// <summary>The number of live Engine.IO sessions, including pending namespace connections.</summary>
    public int ConnectionCount => _sessions.Count;

    internal SocketIOOptions      Options  { get; }
    internal NamespaceRegistry    Registry { get; }
    internal IServiceScopeFactory Scopes   { get; }
    internal ILogger              Logger   { get; }

    private bool _stopped;
    private long _nextAcknowledgement;

    private readonly ConcurrentDictionary<string, EngineSession> _sessions = new(StringComparer.Ordinal);
    private readonly Lock                                        _gate     = new();
    private readonly CancellationTokenRegistration               _stopping;

    /// <summary>Creates a server from host services. Applications normally resolve the registered singleton.</summary>
    public SocketIOServer(SocketIOOptions          options,
                          IServiceScopeFactory     scopes,
                          ILogger<SocketIOServer>  logger,
                          IHostApplicationLifetime lifetime,
                          IServiceProvider         services)
    {
        Options   = options;
        Scopes    = scopes;
        Logger    = logger;
        Registry  = services.GetRequiredService<NamespaceRegistry>();
        _stopping = lifetime.ApplicationStopping.Register(CloseAll);
    }

    /// <summary>Starts the server. Protocol sessions are created when clients connect.</summary>
    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>Closes sessions and awaits their application callbacks and cleanup.</summary>
    public async Task StopAsync(CancellationToken ct)
    {
        CloseAll();
        await Task.WhenAll(_sessions.Values.Select(session => session.Completion)).WaitAsync(ct);
    }

    /// <summary>Stops accepting sessions and releases the host shutdown registration.</summary>
    public void Dispose()
    {
        CloseAll();
        _stopping.Dispose();
    }

    /// <summary>Enqueues an event for every connected socket in a namespace, optionally selecting a room and excluding one socket ID.</summary>
    /// <remarks>Delivery is best effort and at most once. Rooms and sessions are local to this server process.</remarks>
    public ValueTask BroadcastAsync(string ns, string name, object?[] arguments, string? room = null, string? except = null)
    {
        PacketCodec.ValidateEventName(name);
        var packets = PacketCodec.Encode(2, ns, null, new object?[] { name }.Concat(arguments).ToArray(), Options);
        foreach (var session in _sessions.Values)
        {
            if (session.Sockets.TryGetValue(ns, out var socket) && socket.Id != except)
            {
                try
                {
                    socket.Broadcast(packets, room);
                }
                catch (IOException)
                {
                    // An overflowing or disconnected recipient does not prevent delivery to the others.
                }
            }
        }

        return ValueTask.CompletedTask;
    }

    internal EngineSession Create(RequestContext context, bool webSocket)
    {
        lock (_gate)
        {
            if (_stopped || _sessions.Count >= Options.MaxConnections)
            {
                throw new HttpErrorException(StatusCodes.ServiceUnavailable, "Socket.IO connection capacity is exhausted.");
            }

            var session = new EngineSession(this, context.User, webSocket);
            _sessions.TryAdd(session.Id, session);
            session.Start();
            return session;
        }
    }

    internal EngineSession Find(string id) => _sessions.TryGetValue(id, out var session) && !session.Closed.IsCancellationRequested
        ? session
        : throw new HttpErrorException(StatusCodes.BadRequest, "Unknown Engine.IO session.");

    internal void Remove(EngineSession session) => _sessions.TryRemove(session.Id, out _);

    // A client can retain an acknowledgement callback across namespace or transport reconnects.
    internal long AllocateAcknowledgementId() => Interlocked.Increment(ref _nextAcknowledgement);

    private void CloseAll()
    {
        lock (_gate)
        {
            _stopped = true;
            foreach (var session in _sessions.Values)
            {
                session.Close("server shutdown");
            }
        }
    }
}
