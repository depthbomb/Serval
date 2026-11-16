#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

namespace Serval.Http.Constants;

/// <summary>HTTP authentication scheme tokens for Authorization and authentication challenges.</summary>
public static class HttpAuthenticationSchemes
{
    /// <summary>HTTP Basic authentication.</summary>
    public const string Basic = "Basic";

    /// <summary>Bearer token authentication.</summary>
    public const string Bearer = "Bearer";

    /// <summary>HTTP Digest authentication.</summary>
    public const string Digest = "Digest";

    /// <summary>SPNEGO authentication negotiation.</summary>
    public const string Negotiate = "Negotiate";
}
