#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Text.Json;

namespace Serval.SocketIO;

/// <summary>Handles one namespace. Every callback receives a newly created and asynchronously disposed DI scope.</summary>
/// <remarks>Callbacks are ordered per Engine.IO session. Observe cancellation and store durable state outside the handler.</remarks>
public abstract class SocketIOHandler
{
    /// <summary>Validates namespace auth data. Return false to send CONNECT_ERROR. The HTTP principal is available on the socket.</summary>
    public virtual ValueTask<bool> OnConnectingAsync(SocketIOConnection socket, JsonElement auth, CancellationToken ct) => ValueTask.FromResult(true);

    /// <summary>Runs after the namespace connection is accepted.</summary>
    public virtual ValueTask OnConnectedAsync(SocketIOConnection socket, CancellationToken ct) => ValueTask.CompletedTask;

    /// <summary>Handles an application event. Acknowledgements are explicit through the event context.</summary>
    public abstract ValueTask OnEventAsync(SocketIOEvent message, CancellationToken ct);

    /// <summary>Runs after rooms and pending acknowledgements are removed. Its token has a separate cleanup deadline.</summary>
    public virtual ValueTask OnDisconnectedAsync(SocketIOConnection socket, string reason, CancellationToken ct) => ValueTask.CompletedTask;
}
