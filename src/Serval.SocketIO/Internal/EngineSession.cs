#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Serval.SocketIO.Internal;

internal sealed partial class EngineSession
{
    internal string                                           Id         { get; } = Guid.NewGuid().ToString("N");
    internal SocketIOOptions                                  Options    => _server.Options;
    internal CancellationToken                                Closed     => _closed.Token;
    internal ConcurrentDictionary<string, SocketIOConnection> Sockets    { get; }              = new(StringComparer.Ordinal);
    internal Task                                             Completion { get; private set; } = Task.CompletedTask;

    private long          _incomingBytes;
    private long          _outgoingBytes;
    private long          _emptySince = Stopwatch.GetTimestamp();
    private long          _lastPing   = Stopwatch.GetTimestamp();
    private long          _pingSent;
    private long          _binaryStarted;
    private int           _closing;
    private string        _reason = "transport close";
    private SocketPacket? _pendingBinary;
    private int           _packetBytes;

    private readonly SocketIOServer          _server;
    private readonly ClaimsPrincipal         _user;
    private readonly CancellationTokenSource _closed = new();
    private readonly Channel<IncomingPacket> _incoming;
    private readonly Channel<EnginePacket>   _outgoing;
    private readonly Lock                    _sendGate            = new();
    private readonly Lock                    _transportGate       = new();
    private readonly SemaphoreSlim           _receiveGate         = new(1);
    private readonly List<byte[]>            _attachments         = [];
    private readonly HashSet<string>         _connectedNamespaces = new(StringComparer.Ordinal);

    private static readonly string[] WebSocketUpgrades = ["websocket"];

    internal EngineSession(SocketIOServer server, ClaimsPrincipal user, bool webSocket)
    {
        _server   = server;
        _user     = new ClaimsPrincipal(user.Identities.Select(identity => identity.Clone()));
        _upgraded = webSocket;
        _incoming = Channel.CreateBounded<IncomingPacket>(new BoundedChannelOptions(Options.MaxQueuedPackets)
        {
            SingleReader = true,
            FullMode     = BoundedChannelFullMode.Wait,
        });
        _outgoing = Channel.CreateBounded<EnginePacket>(new BoundedChannelOptions(Options.MaxQueuedPackets)
        {
            FullMode = BoundedChannelFullMode.Wait,
        });
    }

    internal void Start() => Completion = RunAsync();

    internal long NextAcknowledgementId()
    {
        var id = _server.AllocateAcknowledgementId();
        if (id > 9_007_199_254_740_991)
        {
            Close("acknowledgement IDs exhausted");
            throw new IOException("The session exhausted its acknowledgement IDs.");
        }

        return id;
    }

    internal void ReportCancellationError(Exception exception)
        => _server.Logger.LogWarning(exception, "Socket.IO cancellation callback failed for {SessionId}.", Id);

    internal EnginePacket OpenPacket(bool polling) => EnginePacket.Text("0" + JsonSerializer.Serialize(new
    {
        sid          = Id,
        upgrades     = polling && Options.EnableWebSockets ? WebSocketUpgrades : [],
        pingInterval = (int)Options.PingInterval.TotalMilliseconds,
        pingTimeout  = (int)Options.PingTimeout.TotalMilliseconds,
        maxPayload   = Options.MaxPayloadBytes,
    }));

    internal void Enqueue(params EnginePacket[] packets)
    {
        var failed = false;
        lock (_sendGate)
        {
            if (Closed.IsCancellationRequested)
            {
                throw new IOException("The Engine.IO session is closed.");
            }

            foreach (var packet in packets)
            {
                var bytes = Interlocked.Add(ref _outgoingBytes, packet.Data.Length);
                if (bytes > Options.MaxQueuedBytes || !_outgoing.Writer.TryWrite(packet))
                {
                    Interlocked.Add(ref _outgoingBytes, -packet.Data.Length);
                    failed = true;
                    break;
                }
            }
        }

        if (failed)
        {
            Close("outgoing queue overflow");
            throw new IOException("The client is not consuming messages quickly enough.");
        }
    }

    internal void Close(string reason)
    {
        if (Interlocked.Exchange(ref _closing, 1) != 0)
        {
            return;
        }

        _reason = reason;
        if (_server.Logger.IsEnabled(LogLevel.Debug))
        {
            _server.Logger.LogDebug("Socket.IO session {SessionId} closed: {Reason}.", Id, reason);
        }

        _incoming.Writer.TryComplete();
        lock (_sendGate)
        {
            _outgoing.Writer.TryComplete();
        }

        try
        {
            _closed.Cancel();
        }
        catch (AggregateException exception)
        {
            _server.Logger.LogWarning(exception, "Socket.IO cancellation callback failed for {SessionId}.", Id);
        }

        foreach (var socket in Sockets.Values)
        {
            socket.Deactivate();
        }
    }

    internal async ValueTask ReceiveAsync(EnginePacket packet, CancellationToken ct)
    {
        await _receiveGate.WaitAsync(ct);
        try
        {
            Closed.ThrowIfCancellationRequested();
            if (packet.Data.Length > Options.MaxPayloadBytes)
            {
                throw new InvalidDataException("Engine.IO message is too large.");
            }

            if (packet.Binary)
            {
                if (_pendingBinary is null)
                {
                    throw new InvalidDataException("Unexpected binary attachment.");
                }

                _packetBytes = checked(_packetBytes + packet.Data.Length);
                if (_packetBytes > Options.MaxPayloadBytes)
                {
                    throw new InvalidDataException("Reconstructed packet is too large.");
                }

                _attachments.Add(packet.Data);
                if (_attachments.Count == _pendingBinary.AttachmentCount)
                {
                    Dispatch(_pendingBinary, [.. _attachments], _packetBytes);
                    _pendingBinary = null;
                    _attachments.Clear();
                    Interlocked.Exchange(ref _binaryStarted, 0);
                }

                return;
            }

            var text = EnginePacket.Utf8.GetString(packet.Data);
            switch (text)
            {
                case "1":
                    Close("client transport close");
                    return;
                case "3" when Interlocked.Read(ref _pingSent) != 0:
                    Interlocked.Exchange(ref _pingSent, 0);
                    return;
            }

            if (text.Length == 0 || text[0] != '4' || _pendingBinary is not null)
            {
                throw new InvalidDataException("Unexpected Engine.IO packet.");
            }

            var decoded = PacketCodec.Decode(text[1..], Options);
            if (decoded.Type is 5 or 6)
            {
                PacketCodec.ValidatePlaceholders(decoded.Data, decoded.AttachmentCount);
            }

            if (decoded.AttachmentCount > 0)
            {
                _pendingBinary = decoded;
                _packetBytes   = packet.Data.Length;
                Interlocked.Exchange(ref _binaryStarted, Stopwatch.GetTimestamp());
            }
            else
            {
                Dispatch(decoded, [], packet.Data.Length);
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException or FormatException or OverflowException or System.Text.DecoderFallbackException)
        {
            Close("invalid packet");
            throw new InvalidDataException("Invalid Socket.IO packet.", exception);
        }
        finally
        {
            _receiveGate.Release();
        }
    }

    internal void RequestDisconnect(SocketIOConnection socket)
    {
        if (!Closed.IsCancellationRequested && socket.IsConnected)
        {
            socket.Deactivate();
            QueueIncoming(new IncomingPacket(new SocketPacket(7, socket.Namespace, null, default, 0), [], 0, socket));
        }
    }

    private void Dispatch(SocketPacket packet, byte[][] attachments, int size)
    {
        // Acknowledgements bypass application handlers, which may themselves be awaiting a client reply.
        switch (packet.Type)
        {
            case 3 or 6:
                if (!Sockets.TryGetValue(packet.Namespace, out var socket) || !socket.IsConnected)
                {
                    // A reply can arrive after its namespace was disconnected.
                    return;
                }

                socket.Acknowledge(packet.Id!.Value, new SocketIOArguments(packet.Data, attachments, Options.Json));
                return;
            case 1 when Sockets.TryGetValue(packet.Namespace, out var disconnected):
                disconnected.Deactivate();
                break;
        }

        QueueIncoming(new IncomingPacket(packet, attachments, size));
    }

    private void QueueIncoming(IncomingPacket packet)
    {
        if (Interlocked.Add(ref _incomingBytes, packet.Size) > Options.MaxQueuedBytes || !_incoming.Writer.TryWrite(packet))
        {
            Interlocked.Add(ref _incomingBytes, -packet.Size);

            Close("incoming queue overflow");

            throw new IOException("Too many pending Socket.IO events.");
        }
    }

    private async Task RunAsync()
    {
        var heartbeat = HeartbeatAsync();
        try
        {
            await foreach (var incoming in _incoming.Reader.ReadAllAsync(Closed))
            {
                Interlocked.Add(ref _incomingBytes, -incoming.Size);
                Closed.ThrowIfCancellationRequested();
                await HandleAsync(incoming);
            }
        }
        catch (OperationCanceledException) when (Closed.IsCancellationRequested) {}
        catch (Exception exception)
        {
            _server.Logger.LogWarning(exception, "Socket.IO session {SessionId} failed.", Id);
            Close("handler or protocol error");
        }
        finally
        {
            Close(_reason);
            await heartbeat;
            foreach (var socket in Sockets.Values)
            {
                await DisconnectAsync(socket, _reason, false);
            }

            // Shutdown has already cancelled Closed; cleanup must still release retained packets and the session.
            await _receiveGate.WaitAsync(CancellationToken.None);

            try
            {
                _pendingBinary = null;
                _attachments.Clear();

                while (_incoming.Reader.TryRead(out var incoming))
                {
                    Interlocked.Add(ref _incomingBytes, -incoming.Size);
                }

                lock (_sendGate)
                {
                    while (_outgoing.Reader.TryRead(out var outgoing))
                    {
                        Interlocked.Add(ref _outgoingBytes, -outgoing.Data.Length);
                    }
                }
            }
            finally
            {
                _receiveGate.Release();
            }

            _server.Remove(this);
        }
    }

    private async ValueTask HandleAsync(IncomingPacket incoming)
    {
        var packet = incoming.Packet;
        switch (packet.Type)
        {
            case 7:
            {
                if (Sockets.TryGetValue(packet.Namespace, out var target) && ReferenceEquals(target, incoming.Target))
                {
                    await DisconnectAsync(target, "server namespace disconnect", true);
                }

                return;
            }

            case 0:
            {
                if (Sockets.ContainsKey(packet.Namespace))
                {
                    throw new InvalidDataException("The namespace is already connected.");
                }

                if (Sockets.Count >= Options.MaxNamespacesPerConnection || !_server.Registry.Handlers.TryGetValue(packet.Namespace, out var type))
                {
                    Enqueue(PacketCodec.Encode(4, packet.Namespace, null, new
                    {
                        message = "Invalid namespace",
                    }, Options));
                    return;
                }

                var socket   = new SocketIOConnection(this, packet.Namespace, type, _user);
                var accepted = false;
                try
                {
                    await InvokeAsync(socket, async (handler, ct) => accepted = await handler.OnConnectingAsync(socket, packet.Data, ct));
                    if (!accepted)
                    {
                        Enqueue(PacketCodec.Encode(4, packet.Namespace, null, new
                        {
                            message = "Not authorized",
                        }, Options));
                        return;
                    }

                    Closed.ThrowIfCancellationRequested();
                    socket.Activate();
                    Sockets.TryAdd(packet.Namespace, socket);
                    _connectedNamespaces.Add(packet.Namespace);
                    Enqueue(PacketCodec.Encode(0, packet.Namespace, null, new
                    {
                        sid = socket.Id,
                    }, Options));
                    await InvokeAsync(socket, (handler, ct) => handler.OnConnectedAsync(socket, ct));
                }
                finally
                {
                    if (!Sockets.ContainsKey(packet.Namespace))
                    {
                        socket.Deactivate();
                    }
                }

                return;
            }
        }

        if (!Sockets.TryGetValue(packet.Namespace, out var connected))
        {
            if (_connectedNamespaces.Contains(packet.Namespace))
            {
                // Events already in flight when a namespace closes must not close its siblings.
                return;
            }

            throw new InvalidDataException("Packet received before namespace connection.");
        }

        if (packet.Type == 1)
        {
            await DisconnectAsync(connected, "client namespace disconnect", false);
            return;
        }

        if (!connected.IsConnected)
        {
            return;
        }

        var message = new SocketIOEvent(connected, packet.Data[0].GetString()!, new SocketIOArguments(packet.Data, incoming.Attachments, Options.Json, 1), packet.Id);
        await InvokeAsync(connected, (handler, ct) => handler.OnEventAsync(message, ct));
    }

    private async ValueTask InvokeAsync(SocketIOConnection socket, Func<SocketIOHandler, CancellationToken, ValueTask> callback, bool cleanup = false)
    {
        await using var scope   = _server.Scopes.CreateAsyncScope();
        using var       timeout = cleanup ? new CancellationTokenSource() : CancellationTokenSource.CreateLinkedTokenSource(Closed, socket.ConnectionClosed);

        timeout.CancelAfter(Options.HandlerTimeout);

        var handler = (SocketIOHandler)scope.ServiceProvider.GetRequiredService(socket.HandlerType);
        try
        {
            await callback(handler, timeout.Token);
            if (!cleanup)
            {
                timeout.Token.ThrowIfCancellationRequested();
            }
        }
        catch (OperationCanceledException) when (!cleanup && socket.ConnectionClosed.IsCancellationRequested && !Closed.IsCancellationRequested) {}
    }

    private async ValueTask DisconnectAsync(SocketIOConnection socket, string reason, bool notify)
    {
        if (!Sockets.TryRemove(socket.Namespace, out _))
        {
            return;
        }

        socket.Deactivate();
        if (notify && !Closed.IsCancellationRequested)
        {
            try
            {
                Enqueue(PacketCodec.Encode(1, socket.Namespace, null, null, Options));
            }
            catch (IOException)
            {
                // Queue overflow closes the transport, but this namespace still needs its cleanup callback.
            }
        }

        Interlocked.Exchange(ref _emptySince, Stopwatch.GetTimestamp());

        try
        {
            await InvokeAsync(socket, (handler, ct) => handler.OnDisconnectedAsync(socket, reason, ct), cleanup: true);
        }
        catch (Exception exception)
        {
            _server.Logger.LogWarning(exception, "Socket.IO disconnect callback failed for {SocketId}.", socket.Id);
        }
    }

    private async Task HeartbeatAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(Math.Min(1000, Options.PingInterval.TotalMilliseconds)));
        try
        {
            while (await timer.WaitForNextTickAsync(Closed))
            {
                var ping   = Interlocked.Read(ref _pingSent);
                var binary = Interlocked.Read(ref _binaryStarted);
                if (ping   != 0     && Stopwatch.GetElapsedTime(ping)                              >= Options.PingTimeout    ||
                    binary != 0     && Stopwatch.GetElapsedTime(binary)                            >= Options.PayloadTimeout ||
                    Sockets.IsEmpty && Stopwatch.GetElapsedTime(Interlocked.Read(ref _emptySince)) >= Options.ConnectTimeout)
                {
                    Close("heartbeat or connection timeout");
                    return;
                }

                if (ping == 0 && Stopwatch.GetElapsedTime(_lastPing) >= Options.PingInterval)
                {
                    _lastPing = Stopwatch.GetTimestamp();
                    Interlocked.Exchange(ref _pingSent, _lastPing);
                    Enqueue(EnginePacket.Text("2"));
                }
            }
        }
        catch (OperationCanceledException) when (Closed.IsCancellationRequested) {}
        catch (IOException)
        {
            Close("heartbeat failed");
        }
    }

    private sealed record IncomingPacket(SocketPacket Packet, byte[][] Attachments, int Size, SocketIOConnection? Target = null);
}
