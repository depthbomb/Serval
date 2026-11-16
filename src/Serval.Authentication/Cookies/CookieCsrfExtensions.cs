#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Security.Cryptography;
using System.Text;
using Serval.Http.Constants;
using Serval.Middleware;

namespace Serval.Authentication.Cookies;

/// <summary>Exposes and validates the CSRF token stored in an authenticated cookie session.</summary>
public static class CookieCsrfExtensions
{
    extension(RequestContext context)
    {
        /// <summary>Authenticates the cookie session and returns its CSRF token.</summary>
        /// <param name="scheme">The cookie scheme, or null for the authentication default.</param>
        /// <returns>The token to return to the client and submit on state-changing requests.</returns>
        /// <remarks>Sets Cache-Control to no-store. Call before the response starts.</remarks>
        /// <exception cref="InvalidOperationException">
        ///     There is no authenticated cookie session, authentication is unavailable, or response headers are already
        ///     frozen.
        /// </exception>
        public async ValueTask<string> GetCsrfTokenAsync(string? scheme = null)
        {
            var result = await context.AuthenticateAsync(scheme);
            if (result.Ticket is not {} ticket || !ticket.Properties.Items.TryGetValue(CookieAuthenticationHandler.CsrfProperty, out var token))
            {
                throw new InvalidOperationException("An authenticated cookie session is required.");
            }

            context.Response.Headers[HeaderNames.CacheControl] = CacheControlDirectives.NoStore;
            return token;
        }

        /// <summary>Authenticates the cookie session and compares its CSRF token in constant time.</summary>
        /// <param name="token">The submitted token; null or an invalid length fails validation.</param>
        /// <param name="scheme">The cookie scheme, or null for the authentication default.</param>
        /// <returns>True when the supplied token matches the current session.</returns>
        /// <remarks>
        ///     Successful authentication updates the current principal through the authentication service.
        /// </remarks>
        public async ValueTask<bool> ValidateCsrfTokenAsync(string? token, string? scheme = null)
        {
            if (token is null || token.Length != 43)
            {
                return false;
            }

            var result = await context.AuthenticateAsync(scheme);
            if (result.Ticket is not {} ticket || !ticket.Properties.Items.TryGetValue(CookieAuthenticationHandler.CsrfProperty, out var expected))
            {
                return false;
            }

            return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(expected));
        }
    }
}

/// <summary>Requires a matching CSRF header or form field on requests other than GET, HEAD, and OPTIONS.</summary>
public sealed class RequireCsrfAttribute : MiddlewareAttribute
{
    /// <summary>The cookie scheme, or null for the authentication default.</summary>
    public string? Scheme { get; set; }

    /// <summary>The request header carrying the CSRF token. Defaults to X-CSRF-Token.</summary>
    public string HeaderName { get; set; } = HeaderNames.XCsrfToken;

    /// <summary>The form field to read when the header is absent. Defaults to _csrf; null disables form fallback.</summary>
    public string? FormFieldName { get; set; } = "_csrf";

    /// <inheritdoc />
    /// <remarks>Invalid or missing tokens produce status 403 without running the remaining pipeline.</remarks>
    public override async ValueTask InvokeAsync(RequestContext context, RequestDelegate next)
    {
        var safe = context.Request.Method is HttpMethods.Get or HttpMethods.Head or HttpMethods.Options;
        if (safe)
        {
            await next(context);
            return;
        }

        var values = context.Request.Headers.GetValues(HeaderName);
        var token  = values.Count == 1 ? values[0] : null;
        if (values.Count == 0 && FormFieldName is {} name && context.Request.HasFormContentType)
        {
            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            form.TryGet<string>(name, out token);
        }

        if (!await context.ValidateCsrfTokenAsync(token, Scheme))
        {
            context.Response.StatusCode = StatusCodes.Forbidden;
            return;
        }

        await next(context);
    }
}
