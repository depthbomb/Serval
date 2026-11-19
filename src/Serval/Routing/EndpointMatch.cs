#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

namespace Serval.Routing;

/// <summary>Finds an endpoint without executing it or changing the response.</summary>
public interface IEndpointMatcher
{
    /// <summary>Returns the selected endpoint, captured values, and methods supported by the path.</summary>
    public EndpointMatch Match(RequestContext context);
}

/// <summary>An immutable routing decision. An empty method list and null endpoint indicate an unmatched path.</summary>
public sealed class EndpointMatch
{
    /// <summary>The selected endpoint, or null when the path or method did not match.</summary>
    public Endpoint? Endpoint { get; }

    /// <summary>Decoded route values for the selected endpoint.</summary>
    public IReadOnlyDictionary<string, string> RouteValues { get; }

    /// <summary>The methods supported by the matched path, in ordinal order.</summary>
    public IReadOnlyList<string> AllowedMethods { get; }

    /// <summary>Creates a routing decision, copying the supplied values and methods.</summary>
    public EndpointMatch(Endpoint? endpoint, IReadOnlyDictionary<string, string>? routeValues, IEnumerable<string> allowedMethods)
    {
        ArgumentNullException.ThrowIfNull(allowedMethods);
        Endpoint       = endpoint;
        RouteValues    = new Dictionary<string, string>(routeValues ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase).AsReadOnly();
        AllowedMethods = Array.AsReadOnly([.. allowedMethods.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)]);
    }

    /// <summary>Applies endpoint metadata, body limits, and deadline before application middleware reads the body.</summary>
    public void Apply(RequestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var endpoint = Endpoint ?? throw new InvalidOperationException("No endpoint was selected.");
        context.ApplyEndpoint(endpoint);
        context.RouteValues = RouteValues;
    }

    /// <summary>Applies the selected endpoint's policies and executes its middleware and handler.</summary>
    public ValueTask ExecuteAsync(RequestContext context)
    {
        Apply(context);
        return Endpoint!.Pipeline(context);
    }
}
