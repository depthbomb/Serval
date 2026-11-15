#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Net;
using Serval.Http;
using Serval.Http.Constants;

namespace Serval.Middleware;

internal sealed class ForwardedHeadersMiddleware
{
    private readonly int                _limit;
    private readonly HashSet<IPAddress> _proxies;
    private readonly IPNetwork[]        _networks;
    private readonly HashSet<string>    _hosts;

    internal ForwardedHeadersMiddleware(ForwardedHeadersOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.ForwardLimit);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.ForwardLimit, 128);

        _limit    = options.ForwardLimit;
        _proxies  = options.KnownProxies.Select(Normalize).ToHashSet();
        _networks = options.KnownNetworks.ToArray();
        _hosts    = [with(StringComparer.OrdinalIgnoreCase), .. options.AllowedHosts];
        foreach (var host in _hosts)
        {
            ValidateHost(host, "https");
        }
    }

    internal ValueTask InvokeAsync(RequestContext context, RequestDelegate next)
    {
        var request = context.Request;
        if (request.ForwardingApplied)
        {
            throw new InvalidOperationException("Forwarded headers can only be applied once per request.");
        }

        request.ForwardingApplied = true;
        var peer = request.Connection.RemoteAddress;
        if (peer is null || !IsTrusted(peer))
        {
            return next(context);
        }

        var addresses = Values(request.Headers, HeaderNames.XForwardedFor);
        if (addresses.Length == 0)
        {
            return next(context);
        }

        var schemes  = Values(request.Headers, HeaderNames.XForwardedProto);
        var hosts    = Values(request.Headers, HeaderNames.XForwardedHost);
        var prefixes = Values(request.Headers, "X-Forwarded-Prefix");
        if (new[] { schemes, hosts, prefixes }.Any(values => values.Length != 0 && values.Length != addresses.Length))
        {
            throw Invalid("Forwarded header lists must describe the same number of hops.");
        }

        var scheme = request.Scheme;
        var host   = request.Host;
        var prefix = request.PathBase;
        for (var index = addresses.Length - 1; index >= 0 && addresses.Length - index <= _limit && IsTrusted(peer); index--)
        {
            if (!IPAddress.TryParse(addresses[index], out var address))
            {
                if (!IPEndPoint.TryParse(addresses[index], out var endpoint))
                {
                    throw Invalid("Invalid forwarded client address.");
                }

                address = endpoint.Address;
            }

            if (schemes.Length > 0)
            {
                scheme = schemes[index].ToLowerInvariant();
                if (scheme is not ("http" or "https"))
                {
                    throw Invalid("Invalid forwarded scheme.");
                }
            }

            if (hosts.Length > 0)
            {
                host = hosts[index];
                var uri = ValidateHost(host, scheme);
                if (_hosts.Count > 0 && !_hosts.Contains(uri.IdnHost) && !_hosts.Contains(host))
                {
                    throw Invalid("The forwarded host is not allowed.");
                }
            }

            if (prefixes.Length > 0)
            {
                prefix = ValidatePrefix(prefixes[index]);
            }

            peer = Normalize(address);
        }

        // Commit only after every consumed hop has passed validation.
        request.ClientAddress = peer;
        request.Scheme        = scheme;
        request.Host          = host;
        request.PathBase      = prefix;
        return next(context);
    }

    private bool IsTrusted(IPAddress address)
    {
        var normalized = Normalize(address);
        return _proxies.Contains(normalized) || _networks.Any(network => network.Contains(normalized) || network.Contains(address));
    }

    private static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private static string[] Values(Headers headers, string name)
        => headers.GetValues(name).SelectMany(value => value.Split(',', StringSplitOptions.TrimEntries)).ToArray();

    private static Uri ValidateHost(string host, string scheme)
    {
        if (host.Length == 0                                                                                                                  ||
            host.Any(character => char.IsWhiteSpace(character) || char.IsControl(character) || character is '/' or '\\' or '@' or '?' or '#') ||
            !Uri.TryCreate(scheme + "://" + host + "/", UriKind.Absolute, out var uri)                                                        ||
            uri.UserInfo.Length != 0                                                                                                          ||
            uri.Host.Length     == 0                                                                                                          ||
            uri.AbsolutePath    != "/")
        {
            throw Invalid("Invalid forwarded host.");
        }

        return uri;
    }

    private static string ValidatePrefix(string prefix)
    {
        if (prefix.Length == 0 || prefix == "/")
        {
            return "";
        }

        if (!prefix.StartsWith('/') || prefix.StartsWith("//", StringComparison.Ordinal)
                                    || prefix.Any(character => char.IsWhiteSpace(character) || char.IsControl(character) || character is '\\' or '?' or '#')
                                    || !Uri.TryCreate("http://localhost" + prefix, UriKind.Absolute, out var uri) || uri.AbsolutePath != prefix
                                    || Uri.UnescapeDataString(prefix).Split('/').Any(segment => segment is "." or ".." || segment.Contains('\\') || segment.Any(char.IsControl)))
        {
            throw Invalid("Invalid forwarded path prefix.");
        }

        return prefix.TrimEnd('/');
    }

    private static HttpErrorException Invalid(string message) => new(StatusCodes.BadRequest, message);
}
