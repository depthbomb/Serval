#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Net;

namespace Serval.Http;

public sealed partial class Request
{
    /// <summary>The original transport connection, never replaced by proxy information.</summary>
    public RequestConnection Connection { get; }

    /// <summary>The effective client address, updated only by configured trusted-proxy middleware.</summary>
    public IPAddress? ClientAddress { get; internal set; }

    /// <summary>The effective HTTP or HTTPS scheme.</summary>
    public string Scheme { get; internal set; }

    /// <summary>The effective authority, including an explicit port when supplied.</summary>
    public string Host { get; internal set; }

    /// <summary>A trusted proxy's external mount prefix, without a trailing slash. Empty by default.</summary>
    public string PathBase { get; internal set; } = "";

    /// <summary>
    ///     The effective public URL, combining proxy information with the transport's application-relative path and
    ///     query.
    /// </summary>
    /// <remarks>Url remains the original transport URL for routing and signature validation.</remarks>
    public Uri PublicUrl => new($"{Scheme}://{Host}{PathBase}{Url.PathAndQuery}");

    internal bool ForwardingApplied { get; set; }
}
