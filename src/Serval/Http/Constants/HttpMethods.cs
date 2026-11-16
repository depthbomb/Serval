#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

namespace Serval.Http.Constants;

/// <summary>HTTP method names for route registration and request comparisons.</summary>
public static class HttpMethods
{
    /// <summary>The <c>GET</c> HTTP method.</summary>
    public const string Get = "GET";

    /// <summary>The safe, idempotent <c>QUERY</c> HTTP method for queries carried in request content.</summary>
    public const string Query = "QUERY";

    /// <summary>The <c>HEAD</c> HTTP method.</summary>
    public const string Head = "HEAD";

    /// <summary>The <c>POST</c> HTTP method.</summary>
    public const string Post = "POST";

    /// <summary>The <c>PUT</c> HTTP method.</summary>
    public const string Put = "PUT";

    /// <summary>The <c>PATCH</c> HTTP method.</summary>
    public const string Patch = "PATCH";

    /// <summary>The <c>DELETE</c> HTTP method.</summary>
    public const string Delete = "DELETE";

    /// <summary>The <c>OPTIONS</c> HTTP method.</summary>
    public const string Options = "OPTIONS";

    /// <summary>The <c>CONNECT</c> HTTP method.</summary>
    public const string Connect = "CONNECT";

    /// <summary>The <c>TRACE</c> HTTP method.</summary>
    public const string Trace = "TRACE";
}
