#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Security.Claims;
using Serval.SocketIO.Internal;

namespace Serval.SocketIO;

/// <summary>A namespace connection. Safe to retain for background sends; it contains no request-scoped services.</summary>
public sealed class SocketIOConnection
{
    /// <summary>The namespace socket ID, distinct from the underlying Engine.IO session ID.</summary>
    public string Id { get; } = Guid.NewGuid().ToString("N");

    /// <summary>The case-sensitive namespace name.</summary>
    public string Namespace { get; }

    /// <summary>A copy of the handshake's HTTP principal. OnConnectingAsync may replace it after validating auth data.</summary>
    public ClaimsPrincipal User { get; set; }

    /// <summary>Signals namespace disconnection or transport shutdown.</summary>
    public CancellationToken ConnectionClosed { get; }

    /// <summary>Whether the namespace is currently connected.</summary>
    public bool IsConnected => Volatile.Read(ref _connected);

    /// <summary>A snapshot of the rooms this socket belongs to.</summary>
    public IReadOnlyCollection<string> Rooms
    {
        get
        {
            lock (_gate)
            {
                return [.. _rooms];
            }
        }
    }

    internal EngineSession Session     { get; }
    internal Type          HandlerType { get; }

    private bool _connected;
    private int  _deactivated;

    private readonly Lock                                                      _gate  = new();
    private readonly HashSet<string>                                           _rooms = new(StringComparer.Ordinal);
    private readonly Dictionary<long, TaskCompletionSource<SocketIOArguments>> _acks  = [];
    private readonly CancellationTokenSource                                   _closed;

    internal SocketIOConnection(EngineSession session, string ns, Type handlerType, ClaimsPrincipal user)
    {
        Session          = session;
        Namespace        = ns;
        HandlerType      = handlerType;
        User             = new ClaimsPrincipal(user.Identities.Select(identity => identity.Clone()));
        _closed          = CancellationTokenSource.CreateLinkedTokenSource(session.Closed);
        ConnectionClosed = _closed.Token;
    }

    /// <summary>Enqueues a named event. Completion means queued, not delivered. Slow peers are disconnected.</summary>
    public ValueTask EmitAsync(string name, params object?[] arguments)
    {
        PacketCodec.ValidateEventName(name);
        Send(2, null, [.. new object?[] { name }.Concat(arguments)]);
        return ValueTask.CompletedTask;
    }

    /// <summary>Sends an event and waits for the client's acknowledgement, with a deadline and cancellation.</summary>
    public async ValueTask<SocketIOArguments> EmitWithAcknowledgementAsync(string name, object?[] arguments, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        PacketCodec.ValidateEventName(name);
        var deadline = timeout ?? Session.Options.AckTimeout;
        if (deadline.TotalMilliseconds is < 1 or > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        long id;
        var  completion = new TaskCompletionSource<SocketIOArguments>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            EnsureConnected();
            if (_acks.Count >= Session.Options.MaxPendingAcknowledgements)
            {
                throw new InvalidOperationException("Too many pending acknowledgements.");
            }

            id = Session.NextAcknowledgementId();
            _acks.Add(id, completion);
        }

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, ConnectionClosed);
            Send(2, id, [.. new object?[] { name }.Concat(arguments)]);
            return await completion.Task.WaitAsync(deadline, linked.Token);
        }
        finally
        {
            lock (_gate)
            {
                _acks.Remove(id);
            }
        }
    }

    /// <summary>Adds this socket to an in-memory room scoped to its namespace.</summary>
    public void Join(string room)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(room);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(room.Length, 256);
        lock (_gate)
        {
            EnsureConnected();
            if (!_rooms.Contains(room) && _rooms.Count >= Session.Options.MaxRoomsPerConnection)
            {
                throw new InvalidOperationException("Too many rooms for this socket.");
            }

            _rooms.Add(room);
        }
    }

    /// <summary>Removes this socket from a room.</summary>
    public void Leave(string room)
    {
        lock (_gate)
        {
            _rooms.Remove(room);
        }
    }

    /// <summary>Disconnects this namespace, or closes the entire transport so the client may reconnect.</summary>
    public void Disconnect(bool closeTransport = false)
    {
        if (closeTransport)
        {
            Session.Close("server transport close");
        }
        else
        {
            Session.RequestDisconnect(this);
        }
    }

    internal void Activate()
    {
        lock (_gate)
        {
            _rooms.Add(Id);
            _connected = true;
        }
    }

    internal void Deactivate()
    {
        if (Interlocked.Exchange(ref _deactivated, 1) != 0)
        {
            return;
        }

        lock (_gate)
        {
            _connected = false;
            _rooms.Clear();
            _acks.Clear();
        }

        try
        {
            _closed.Cancel();
        }
        catch (AggregateException exception)
        {
            Session.ReportCancellationError(exception);
        }
        finally
        {
            _closed.Dispose();
        }
    }

    internal bool IsInRoom(string room)
    {
        lock (_gate)
        {
            return _rooms.Contains(room);
        }
    }

    internal void Acknowledge(long id, SocketIOArguments arguments)
    {
        lock (_gate)
        {
            if (_acks.Remove(id, out var completion))
            {
                completion.TrySetResult(arguments);
            }
        }
    }

    internal void Send(int type, long? id, object?[] arguments)
    {
        var packets = PacketCodec.Encode(type, Namespace, id, arguments, Session.Options);
        lock (_gate)
        {
            EnsureConnected();
            Session.Enqueue(packets);
        }
    }

    internal void Broadcast(EnginePacket[] packets, string? room)
    {
        lock (_gate)
        {
            if (IsConnected && (room is null || IsInRoom(room)))
            {
                Session.Enqueue(packets);
            }
        }
    }

    private void EnsureConnected()
    {
        if (!IsConnected || ConnectionClosed.IsCancellationRequested)
        {
            throw new IOException("The Socket.IO namespace is not connected.");
        }
    }
}
