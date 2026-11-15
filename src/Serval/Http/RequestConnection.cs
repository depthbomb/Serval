#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Net;

namespace Serval.Http;

/// <summary>The socket endpoints reported by the transport, unaffected by forwarded headers.</summary>
public sealed class RequestConnection
{
    /// <summary>The immediate peer address, or null when unavailable.</summary>
    public IPAddress? RemoteAddress { get; }

    /// <summary>The immediate peer port, or null when unavailable.</summary>
    public int? RemotePort { get; }

    /// <summary>The local listening address, or null when unavailable.</summary>
    public IPAddress? LocalAddress { get; }

    /// <summary>The local listening port, or null when unavailable.</summary>
    public int? LocalPort { get; }

    internal static readonly RequestConnection Unknown = new();

    /// <summary>Captures transport endpoints. Null endpoints represent unavailable connection information.</summary>
    public RequestConnection(IPEndPoint? remoteEndPoint = null, IPEndPoint? localEndPoint = null)
    {
        RemoteAddress = remoteEndPoint?.Address;
        RemotePort    = remoteEndPoint?.Port;
        LocalAddress  = localEndPoint?.Address;
        LocalPort     = localEndPoint?.Port;
    }
}
