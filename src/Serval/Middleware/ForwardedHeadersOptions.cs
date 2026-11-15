#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Net;

namespace Serval.Middleware;

/// <summary>Controls which proxies may provide X-Forwarded-For, -Proto, -Host, and -Prefix values.</summary>
public sealed class ForwardedHeadersOptions
{
    /// <summary>The maximum number of trusted hops to consume from right to left. Defaults to one.</summary>
    public int ForwardLimit { get; set; } = 1;

    /// <summary>Trusted immediate proxy addresses. No address is trusted by default.</summary>
    public ISet<IPAddress> KnownProxies { get; } = new HashSet<IPAddress>();

    /// <summary>Trusted proxy networks. Keep these limited to your deployment's proxies.</summary>
    public IList<IPNetwork> KnownNetworks { get; } = new List<IPNetwork>();

    /// <summary>
    ///     Optional allowed forwarded hosts, compared without case. Entries may include a port; wildcards are
    ///     unsupported.
    /// </summary>
    public ISet<string> AllowedHosts { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}
