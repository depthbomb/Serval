#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Text.Json;

namespace Serval.SocketIO;

/// <summary>Configures Socket.IO protocol 5 over Engine.IO 4. Configure before registration completes.</summary>
public sealed class SocketIOOptions
{
    /// <summary>The HTTP endpoint, relative to the Serval builder's route group.</summary>
    public string Path { get; set; } = "/socket.io/";

    /// <summary>Whether to accept and advertise WebSocket connections and upgrades.</summary>
    public bool EnableWebSockets { get; set; } = true;

    /// <summary>Time between server heartbeats.</summary>
    public TimeSpan PingInterval { get; set; } = TimeSpan.FromSeconds(25);

    /// <summary>Time allowed for a heartbeat reply.</summary>
    public TimeSpan PingTimeout { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>Time allowed to complete a WebSocket upgrade probe.</summary>
    public TimeSpan UpgradeTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Time allowed to connect the first namespace.</summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>Maximum time to receive one HTTP POST body or finish a binary Socket.IO packet.</summary>
    public TimeSpan PayloadTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Cooperative deadline for each application callback, including disconnect callbacks.</summary>
    public TimeSpan HandlerTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Default deadline for server events that request acknowledgements.</summary>
    public TimeSpan AckTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Maximum bytes per HTTP payload, WebSocket message, or reconstructed Socket.IO packet.</summary>
    public int MaxPayloadBytes { get; set; } = 1_000_000;

    /// <summary>Maximum live Engine.IO sessions.</summary>
    public int MaxConnections { get; set; } = 1024;

    /// <summary>Maximum queued incoming events and outgoing Engine.IO packets per session.</summary>
    public int MaxQueuedPackets { get; set; } = 128;

    /// <summary>Maximum queued bytes in each direction per session. Slow peers are disconnected.</summary>
    public int MaxQueuedBytes { get; set; } = 4_000_000;

    /// <summary>Maximum binary attachments per Socket.IO packet.</summary>
    public int MaxAttachments { get; set; } = 32;

    /// <summary>Maximum namespace connections per Engine.IO session.</summary>
    public int MaxNamespacesPerConnection { get; set; } = 16;

    /// <summary>Maximum rooms per namespace connection, including its automatic private room.</summary>
    public int MaxRoomsPerConnection { get; set; } = 128;

    /// <summary>Maximum outstanding server acknowledgement requests per namespace connection.</summary>
    public int MaxPendingAcknowledgements { get; set; } = 128;

    /// <summary>Additional allowed browser origins. Requests without Origin and same-origin requests are allowed.</summary>
    /// <remarks>Use exact origins such as https://app.example.com. Wildcards and the opaque origin null are rejected.</remarks>
    public ISet<string> AllowedOrigins { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>JSON settings for application arguments. Byte arrays are sent as binary attachments.</summary>
    public JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web);

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(Path) || !Path.StartsWith('/') || Path.StartsWith("//", StringComparison.Ordinal) || Path.IndexOfAny(['?', '#', '{', '}']) >= 0)
        {
            throw new ArgumentException("Socket.IO requires a literal application-relative endpoint path.", nameof(Path));
        }

        foreach (var value in new[] { PingInterval, PingTimeout, UpgradeTimeout, ConnectTimeout, PayloadTimeout, HandlerTimeout, AckTimeout })
        {
            if (value.TotalMilliseconds is < 1 or > int.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "Socket.IO timeouts must be positive and fit in milliseconds.");
            }
        }

        foreach (var value in new[] { MaxPayloadBytes, MaxConnections, MaxQueuedPackets, MaxQueuedBytes, MaxAttachments, MaxNamespacesPerConnection, MaxRoomsPerConnection, MaxPendingAcknowledgements })
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
        }

        foreach (var origin in AllowedOrigins)
        {
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.GetLeftPart(UriPartial.Authority) != origin)
            {
                throw new ArgumentException("Allowed origins must be exact HTTP or HTTPS origins.", nameof(AllowedOrigins));
            }
        }

        Json.MakeReadOnly(populateMissingResolver: true);
    }
}
