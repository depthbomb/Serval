#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Collections.Frozen;
using System.Globalization;
using Serval.Http;
using Serval.Http.Constants;

namespace Serval.Routing;

internal sealed class Router : IUrlGenerator, IEndpointMatcher
{
    private readonly EndpointIndex                           _index;
    private readonly FrozenDictionary<string, RouteTemplate> _namedRoutes;
    private readonly ServalOptions                           _options;
    private readonly RoutingMiddleware                       _dispatcher;

    public Router(ControllerRegistry registry, ServalOptions options)
    {
        _options    = options;
        _dispatcher = new RoutingMiddleware(this, options.AutomaticOptions);
        options.Validate();
        var endpoints  = registry.Freeze(options).Select(registration => (Registration: registration, Template: new RouteTemplate(registration.Template, options))).ToArray();
        var duplicates = endpoints.GroupBy(endpoint => (endpoint.Registration.Method, endpoint.Template.Signature)).FirstOrDefault(group => group.Count() > 1);
        if (duplicates is not null)
        {
            throw new InvalidOperationException($"Duplicate controller routes: {string.Join(", ", duplicates.Select(endpoint => endpoint.Registration.Template))}.");
        }

        _namedRoutes = endpoints.Where(endpoint => endpoint.Registration.Name is not null).ToFrozenDictionary(endpoint => endpoint.Registration.Name!, endpoint => endpoint.Template, StringComparer.Ordinal);
        var ordered = endpoints.Select(endpoint => new RouteEndpoint(endpoint.Registration, endpoint.Template)).OrderByDescending(endpoint => endpoint.Template.Precedence, StringComparer.Ordinal).ToArray();
        _index = new EndpointIndex(ordered, 0, options.CaseSensitivePaths ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
    }

    public string Path(string routeName, IReadOnlyDictionary<string, object?>? values = null, IReadOnlyDictionary<string, object?>? query = null)
    {
        if (!_namedRoutes.TryGetValue(routeName, out var template))
        {
            throw new ArgumentException($"Unknown route '{routeName}'.", nameof(routeName));
        }

        var path = template.Generate(values ?? new Dictionary<string, object?>());
        if (query is { Count: > 0 })
        {
            path += "?" + string.Join('&', query.Where(pair => pair.Value is not null).Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(Convert.ToString(pair.Value, CultureInfo.InvariantCulture)!)}"));
        }

        return path;
    }

    public EndpointMatch Match(RequestContext context)
    {
        var path             = context.Request.Url.AbsolutePath;
        var hasTrailingSlash = path.Length > 1 && path.EndsWith('/');
        // Split before decoding so an escaped slash remains part of a single parameter.
        var parts            = path        == "/" ? [] : (hasTrailingSlash ? path[1..^1] : path[1..]).Split('/');
        for (var index = 0; index < parts.Length; index++)
        {
            parts[index] = Uri.UnescapeDataString(parts[index]);
        }

        var                         candidates             = _index.Find(parts);
        var                         allowed                = new HashSet<string>(StringComparer.Ordinal);
        RouteEndpoint?              selected               = null;
        Dictionary<string, string>? selectedValues         = null;
        var                         selectedIsHeadFallback = false;
        foreach (var endpoint in candidates)
        {
            // Bound cumulative constraint work, even when many regex routes each consume their own timeout.
            context.RequestAborted.ThrowIfCancellationRequested();
            if (!endpoint.Template.TryMatch(parts, hasTrailingSlash, out var values))
            {
                continue;
            }

            allowed.Add(endpoint.Method);
            var fallback = _options.AutomaticHead && context.Request.Method == HttpMethods.Head && endpoint.Method == HttpMethods.Get;
            if (endpoint.Method != context.Request.Method && !fallback)
            {
                continue;
            }

            if (selected is not null && selected.Template.Precedence == endpoint.Template.Precedence && selectedIsHeadFallback == fallback)
            {
                throw new InvalidOperationException($"Ambiguous routes '{selected.Template.Text}' and '{endpoint.Template.Text}'.");
            }

            if (selected is null || selectedIsHeadFallback && !fallback)
            {
                selected               = endpoint;
                selectedValues         = values;
                selectedIsHeadFallback = fallback;
            }
        }

        if (allowed.Count == 0)
        {
            return new EndpointMatch(null, null, allowed);
        }

        if (_options.AutomaticHead && allowed.Contains(HttpMethods.Get))
        {
            allowed.Add(HttpMethods.Head);
        }

        return new EndpointMatch(selected?.Description, selectedValues, allowed);
    }

    public ValueTask<bool> TryDispatchAsync(RequestContext context)
        => _dispatcher.TryHandleAsync(context);

    private sealed record RouteEndpoint(Endpoint Description, RouteTemplate Template)
    {
        public string Method => Description.Method;
    }

    private sealed class EndpointIndex
    {
        private readonly RouteEndpoint[]                         _candidates;
        private readonly FrozenDictionary<string, EndpointIndex> _children;

        public EndpointIndex(RouteEndpoint[] endpoints, int depth, StringComparer comparer)
        {
            _candidates = [.. endpoints.Where(endpoint => endpoint.Template.LiteralPrefix.Length <= depth)];
            _children = endpoints.Where(endpoint => endpoint.Template.LiteralPrefix.Length > depth)
                                 .GroupBy(endpoint => endpoint.Template.LiteralPrefix[depth], comparer)
                                 .ToFrozenDictionary(group => group.Key, group => new EndpointIndex(
                                     [.. group.Concat(_candidates).OrderByDescending(endpoint => endpoint.Template.Precedence, StringComparer.Ordinal)], depth + 1, comparer), comparer);
        }

        public RouteEndpoint[] Find(string[] path)
        {
            var node = this;
            foreach (var part in path)
            {
                if (!node._children.TryGetValue(part, out var child))
                {
                    break;
                }

                node = child;
            }

            // Ancestor parameter and catch-all routes stay eligible at every level.
            return node._candidates;
        }
    }
}
