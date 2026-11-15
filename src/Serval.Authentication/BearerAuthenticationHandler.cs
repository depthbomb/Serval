#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Net.Http.Headers;
using Serval.Http.Constants;

namespace Serval.Authentication;

/// <summary>Configures bearer-token validation and response handling.</summary>
public sealed class BearerAuthenticationOptions
{
    /// <summary>
    ///     The required callback that receives the request and parsed token. It must validate credentials and return an
    ///     authentication result.
    /// </summary>
    public Func<RequestContext, string, ValueTask<AuthenticationResult>>? ValidateToken { get; set; }

    /// <summary>An optional replacement for the default 401 response with a Bearer WWW-Authenticate header.</summary>
    public Func<RequestContext, ValueTask>? OnChallenge { get; set; }

    /// <summary>An optional replacement for the default 403 response.</summary>
    public Func<RequestContext, ValueTask>? OnForbidden { get; set; }

    /// <summary>The positive maximum token length in characters. Defaults to 8,192.</summary>
    public int MaxTokenLength { get; set; } = 8192;

    /// <summary>Whether bearer credentials are rejected on non-HTTPS requests. Defaults to true.</summary>
    public bool RequireHttps { get; set; } = true;

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(ValidateToken);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxTokenLength);
    }
}

/// <summary>Parses one Bearer authorization header and delegates token validation to the application.</summary>
public sealed class BearerAuthenticationHandler : AuthenticationHandler
{
    private readonly BearerAuthenticationOptions _options;

    /// <summary>Creates a bearer handler after validating its options.</summary>
    /// <param name="options">The validator, transport requirement, and response callbacks.</param>
    /// <exception cref="ArgumentNullException">The token validator is absent.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The maximum token length is not positive.</exception>
    public BearerAuthenticationHandler(BearerAuthenticationOptions options)
    {
        options.Validate();
        _options = options;
    }

    /// <inheritdoc />
    public override ValueTask<AuthenticationResult> AuthenticateAsync(RequestContext context, string scheme)
    {
        var values = context.Request.Headers.GetValues(HeaderNames.Authorization);
        if (values.Count == 0)
        {
            return ValueTask.FromResult(AuthenticationResult.Missing());
        }

        if (values.Count != 1 || !AuthenticationHeaderValue.TryParse(values[0], out var header))
        {
            return ValueTask.FromResult(AuthenticationResult.Fail("Invalid authorization header."));
        }

        if (!header.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase))
        {
            return ValueTask.FromResult(AuthenticationResult.Missing());
        }

        if (_options.RequireHttps && context.Request.Url.Scheme != Uri.UriSchemeHttps)
        {
            return ValueTask.FromResult(AuthenticationResult.Fail("Bearer authentication requires HTTPS."));
        }

        var token = header.Parameter;
        if (string.IsNullOrEmpty(token) || token.Length > _options.MaxTokenLength || token.Any(character => !char.IsAsciiLetterOrDigit(character) && !"-._~+/=".Contains(character)))
        {
            return ValueTask.FromResult(AuthenticationResult.Fail("Invalid bearer token."));
        }

        return _options.ValidateToken!(context, token);
    }

    /// <inheritdoc />
    public override ValueTask ChallengeAsync(RequestContext context, string scheme, AuthenticationProperties properties)
    {
        if (_options.OnChallenge is not null)
        {
            return _options.OnChallenge(context);
        }

        context.Response.Headers[HeaderNames.WwwAuthenticate] = "Bearer";
        return base.ChallengeAsync(context, scheme, properties);
    }

    /// <inheritdoc />
    public override ValueTask ForbidAsync(RequestContext context, string scheme)
        => _options.OnForbidden?.Invoke(context) ?? base.ForbidAsync(context, scheme);
}
