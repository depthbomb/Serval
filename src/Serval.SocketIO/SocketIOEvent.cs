#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

namespace Serval.SocketIO;

/// <summary>An application event received on one namespace connection.</summary>
public sealed class SocketIOEvent
{
    /// <summary>The connection that sent the event.</summary>
    public SocketIOConnection Socket { get; }

    /// <summary>The event's case-sensitive name.</summary>
    public string Name { get; }

    /// <summary>The application arguments, excluding the event name.</summary>
    public SocketIOArguments Arguments { get; }

    /// <summary>Whether the sender requested an acknowledgement.</summary>
    public bool ExpectsAcknowledgement => _id is not null;

    private int _acknowledged;

    private readonly long? _id;

    internal SocketIOEvent(SocketIOConnection socket, string name, SocketIOArguments arguments, long? id)
    {
        Socket    = socket;
        Name      = name;
        Arguments = arguments;
        _id       = id;
    }

    /// <summary>Sends one acknowledgement. Throws when no acknowledgement was requested or one was already sent.</summary>
    public ValueTask AcknowledgeAsync(params object?[] arguments)
    {
        if (_id is null || Interlocked.Exchange(ref _acknowledged, 1) != 0)
        {
            throw new InvalidOperationException("This event cannot be acknowledged again.");
        }

        Socket.Send(3, _id, arguments);
        return ValueTask.CompletedTask;
    }
}
