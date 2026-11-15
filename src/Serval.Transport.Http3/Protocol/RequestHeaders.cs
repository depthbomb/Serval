#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Globalization;
using Serval.Http;
using Serval.Http.Constants;

namespace Serval.Transport.Http3.Protocol;

internal sealed record RequestHead(string Method, Uri Url, Headers Headers, long? ContentLength);

internal static class RequestHeaders
{
    public static RequestHead Parse(IReadOnlyList<(string Name, string Value)> fields)
    {
        var headers      = new Headers();
        var pseudo       = new Dictionary<string, string>(StringComparer.Ordinal);
        var ordinarySeen = false;
        foreach (var (name, value) in fields)
        {
            // Validate pseudo-header controls before URI parsing can normalize them away.
            var invalidValue = value.Any(c => c is < ' ' and not '\t' or '\x7f') || value.Length > 0 && (value[0] is ' ' or '\t' || value[^1] is ' ' or '\t');
            if (name.Length == 0 || name.Any(char.IsAsciiLetterUpper) || invalidValue)
            {
                throw Invalid("Invalid HTTP/3 header.");
            }

            if (name.StartsWith(':'))
            {
                if (ordinarySeen || name is not (":method" or ":scheme" or ":authority" or ":path") || !pseudo.TryAdd(name, value))
                {
                    throw Invalid("Invalid pseudo-header.");
                }

                continue;
            }

            ordinarySeen = true;
            var connectionSpecific = name is "connection" or "transfer-encoding" or "keep-alive" or "upgrade" or "proxy-connection";
            if (connectionSpecific || name == "te" && !value.Equals("trailers", StringComparison.OrdinalIgnoreCase))
            {
                throw Invalid("Connection-specific header in HTTP/3.");
            }

            try
            {
                headers.Append(name, value);
            }
            catch (ArgumentException)
            {
                throw Invalid("Invalid HTTP field name or value.");
            }
        }

        var method = Required(pseudo, ":method");
        if (method.Any(c => !char.IsAsciiLetterOrDigit(c) && !"!#$%&'*+-.^_`|~".Contains(c)))
        {
            throw Invalid("Invalid request method.");
        }

        var authority = pseudo.GetValueOrDefault(":authority") ?? headers[HeaderNames.Host] ?? throw Invalid("Missing request authority.");
        var origin    = ParseOrigin(authority);
        // Do not silently discard CONNECT pseudo-headers: proxies must agree on the request target.
        if (method == HttpMethods.Connect && (pseudo.Count != 2 || !pseudo.ContainsKey(":authority") || authority.LastIndexOf(':') <= authority.LastIndexOf(']') || !int.TryParse(authority.AsSpan(authority.LastIndexOf(':') + 1), NumberStyles.None, CultureInfo.InvariantCulture, out _)))
        {
            throw Invalid("CONNECT requires only :method and an :authority with an explicit port.");
        }

        var host = headers[HeaderNames.Host];
        if (host is not null && ParseOrigin(host).Authority != origin.Authority)
        {
            throw Invalid("Host and :authority do not match.");
        }

        // Plain CONNECT has an authority but no scheme or path. Routing may reject it normally.
        var scheme      = method == HttpMethods.Connect ? "https" : Required(pseudo, ":scheme");
        var path        = method == HttpMethods.Connect ? "/" : Required(pseudo, ":path");
        var invalidPath = !path.StartsWith('/') || path.StartsWith("//", StringComparison.Ordinal) || path.Any(c => c is <= ' ' or '\\' or '#');
        if (scheme != "https" || invalidPath || !Uri.TryCreate($"https://{authority}{path}", UriKind.Absolute, out var url))
        {
            throw Invalid("Invalid request scheme or path.");
        }

        long? contentLength = null;
        var   lengths       = headers.GetValues(HeaderNames.ContentLength);
        if (lengths.Count > 0)
        {
            if (lengths.Count != 1 || !long.TryParse(lengths[0], NumberStyles.None, CultureInfo.InvariantCulture, out var length))
            {
                throw Invalid("Invalid Content-Length.");
            }

            contentLength = length;
        }

        headers[HeaderNames.Host] = authority;
        return new RequestHead(method, url, headers, contentLength);
    }

    private static Uri ParseOrigin(string authority)
    {
        var invalid = authority.Length == 0 || authority.Any(c => c is <= ' ' or '/' or '\\' or '?' or '#' or '@');
        if (invalid || !Uri.TryCreate($"https://{authority}", UriKind.Absolute, out var origin) || origin.Host.Length == 0 || origin.UserInfo.Length > 0)
        {
            throw Invalid("Invalid request authority.");
        }

        return origin;
    }

    private static string Required(Dictionary<string, string> fields, string name) => fields.TryGetValue(name, out var value) && value.Length > 0 ? value : throw Invalid($"Missing {name}.");

    private static Http3ProtocolException Invalid(string message) => new(Http3Error.Message, message);
}
