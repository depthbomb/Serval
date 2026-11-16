#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using Serval.Http;
using Serval.Http.Constants;

namespace Serval.SocketIO.Internal;

internal sealed partial class EngineSession
{
    private bool                 _upgraded;
    private bool                 _upgradeActive;
    private bool                 _pollingRead;
    private bool                 _pollingWrite;
    private TaskCompletionSource _pollIdle = CompletedSignal();

    internal void BeginPost()
    {
        lock (_transportGate)
        {
            if (_upgraded || _pollingWrite)
            {
                Close("overlapping polling POST or inactive transport");
                throw new HttpErrorException(StatusCodes.BadRequest, "Only one polling POST is allowed at a time.");
            }

            _pollingWrite = true;
        }
    }

    internal void EndPost()
    {
        lock (_transportGate)
        {
            _pollingWrite = false;
        }
    }

    internal async ValueTask<EnginePacket> PollAsync(Response response, CancellationToken ct)
    {
        lock (_transportGate)
        {
            if (_upgraded || _pollingRead)
            {
                Close("overlapping polling GET or inactive transport");
                throw new HttpErrorException(StatusCodes.BadRequest, "Only one polling GET is allowed at a time.");
            }

            _pollingRead = true;
            _pollIdle    = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, Closed);

            EnginePacket packet;

            try
            {
                packet = await ReadOutgoingAsync(linked.Token);
                if (_outgoing.Reader.TryPeek(out _))
                {
                    var payload = new StringBuilder(packet.PollingText);
                    var size    = packet.PollingSize;
                    while (_outgoing.Reader.TryPeek(out var next) && size + 1 + next.PollingSize <= Options.MaxPayloadBytes)
                    {
                        if (!_outgoing.Reader.TryRead(out next))
                        {
                            break;
                        }

                        Interlocked.Add(ref _outgoingBytes, -next.Data.Length);
                        size += 1 + next.PollingSize;
                        payload.Append('\x1e').Append(next.PollingText);
                    }

                    packet = EnginePacket.Text(payload.ToString());
                }
            }
            catch (Exception exception) when (exception is ChannelClosedException or OperationCanceledException && Closed.IsCancellationRequested)
            {
                packet = EnginePacket.Text("1");
            }

            if (packet.Binary)
            {
                packet = EnginePacket.Text(packet.PollingText);
            }

            await response.WriteAsync(packet.Data, ct);
            await response.CompleteAsync(ct);
            return packet;
        }
        catch (OperationCanceledException)
        {
            Close("polling request aborted");
            throw;
        }
        catch (IOException)
        {
            Close("polling response failed");
            throw;
        }
        finally
        {
            lock (_transportGate)
            {
                _pollingRead = false;
                _pollIdle.TrySetResult();
            }
        }
    }

    internal void BeginUpgrade()
    {
        lock (_transportGate)
        {
            if (_upgraded || _upgradeActive)
            {
                Close("duplicate WebSocket transport");
                throw new HttpErrorException(StatusCodes.BadRequest, "The session already has a WebSocket transport.");
            }

            _upgradeActive = true;
        }
    }

    internal void CancelUpgrade()
    {
        lock (_transportGate)
        {
            _upgradeActive = false;
        }
    }

    internal async Task RunWebSocketAsync(WebSocket socket, bool upgrade, CancellationToken ct)
    {
        using var linked  = CancellationTokenSource.CreateLinkedTokenSource(ct, Closed);
        var       adopted = !upgrade;
        try
        {
            if (upgrade)
            {
                using var probe = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
                probe.CancelAfter(Options.UpgradeTimeout);
                var ping = await ReadWebSocketAsync(socket, probe.Token);
                if (ping is null || ping.Binary || ping.PollingText != "2probe")
                {
                    throw new InvalidDataException("Expected a WebSocket upgrade probe.");
                }

                await SendWebSocketAsync(socket, EnginePacket.Text("3probe"), probe.Token);
                // Release a pending GET so the client can pause polling before it sends the upgrade packet.
                Enqueue(EnginePacket.Text("6"));
                var confirmation = await ReadWebSocketAsync(socket, probe.Token);
                if (confirmation is null || confirmation.Binary || confirmation.PollingText != "5")
                {
                    throw new InvalidDataException("Expected an Engine.IO upgrade packet.");
                }

                Task idle;
                lock (_transportGate)
                {
                    if (_pollingWrite)
                    {
                        throw new InvalidDataException("Polling POST must finish before upgrading.");
                    }

                    _upgraded = true;
                    idle      = _pollIdle.Task;
                }

                adopted = true;
                await idle.WaitAsync(probe.Token);
            }
            else
            {
                await SendWebSocketAsync(socket, OpenPacket(false), linked.Token);
            }

            var sender = SendLoopAsync(socket, linked.Token);
            try
            {
                while (!linked.IsCancellationRequested)
                {
                    var packet = await ReadWebSocketAsync(socket, linked.Token);
                    if (packet is null)
                    {
                        break;
                    }

                    await ReceiveAsync(packet, linked.Token);
                }
            }
            finally
            {
                Close("WebSocket closed");
                await sender;
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or WebSocketException or IOException or InvalidDataException or ChannelClosedException or DecoderFallbackException) {}
        finally
        {
            if (adopted)
            {
                Close("WebSocket closed");
            }

            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try
                {
                    using var close = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Connection closed", close.Token);
                }
                catch (Exception exception) when (exception is OperationCanceledException or WebSocketException) {}
            }
        }
    }

    private async Task SendLoopAsync(WebSocket socket, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var packet = await ReadOutgoingAsync(ct);
                await SendWebSocketAsync(socket, packet, ct);
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or WebSocketException or ChannelClosedException)
        {
            Close("WebSocket send stopped");
        }
    }

    private async ValueTask<EnginePacket> ReadOutgoingAsync(CancellationToken ct)
    {
        var packet = await _outgoing.Reader.ReadAsync(ct);
        Interlocked.Add(ref _outgoingBytes, -packet.Data.Length);
        return packet;
    }

    private async ValueTask<EnginePacket?> ReadWebSocketAsync(WebSocket socket, CancellationToken ct)
    {
        var                   buffer  = ArrayPool<byte>.Shared.Rent(Math.Min(8192, Options.MaxPayloadBytes));
        MemoryStream?         message = null;
        WebSocketMessageType? type    = null;
        try
        {
            while (true)
            {
                var received = await socket.ReceiveAsync(buffer.AsMemory(), ct);
                if (received.MessageType == WebSocketMessageType.Close)
                {
                    return null;
                }

                type ??= received.MessageType;
                if (type != received.MessageType || (message?.Length ?? 0) + received.Count > Options.MaxPayloadBytes)
                {
                    throw new InvalidDataException("Invalid or oversized WebSocket message.");
                }

                if (message is null && received.EndOfMessage)
                {
                    return new EnginePacket([.. buffer.AsSpan(0, received.Count)], type == WebSocketMessageType.Binary);
                }

                message ??= new MemoryStream();
                message.Write(buffer, 0, received.Count);
                if (received.EndOfMessage)
                {
                    return new EnginePacket(message.ToArray(), type == WebSocketMessageType.Binary);
                }
            }
        }
        finally
        {
            message?.Dispose();
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static ValueTask SendWebSocketAsync(WebSocket socket, EnginePacket packet, CancellationToken ct)
        => socket.SendAsync(packet.Data.AsMemory(), packet.Binary ? WebSocketMessageType.Binary : WebSocketMessageType.Text, true, ct);

    private static TaskCompletionSource CompletedSignal()
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        signal.SetResult();
        return signal;
    }
}
