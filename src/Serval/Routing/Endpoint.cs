#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Serval.Http;
using Serval.Middleware;

namespace Serval.Routing;

/// <summary>Describes a selected controller endpoint. Available to middleware through RequestContext.Endpoint.</summary>
public sealed class Endpoint
{
    /// <summary>The registered HTTP method, including GET when handling an automatic HEAD fallback.</summary>
    public string Method { get; }

    /// <summary>The complete route template, including group prefixes.</summary>
    public string Template { get; }

    /// <summary>The optional name used for URL generation.</summary>
    public string? Name { get; }

    /// <summary>The controller resolved from request services.</summary>
    public Type ControllerType { get; }

    /// <summary>Application metadata in registration order. Keep metadata objects immutable after startup.</summary>
    public IReadOnlyList<object> Metadata { get; }

    /// <summary>The endpoint's request deadline, or null to retain the application default.</summary>
    public TimeSpan? RequestTimeout { get; }

    /// <summary>The endpoint's body limit, bounded by the server-wide limit.</summary>
    public long MaxRequestBodyBytes { get; }

    internal FormOptions     Forms    { get; }
    internal RequestDelegate Pipeline { get; }

    internal Endpoint(EndpointBuilder builder, RequestDelegate pipeline)
    {
        Method              = builder.Method;
        Template            = builder.Template;
        Name                = builder.Name;
        ControllerType      = builder.ControllerType;
        Metadata            = Array.AsReadOnly(builder.Metadata.ToArray());
        RequestTimeout      = builder.RequestTimeout;
        MaxRequestBodyBytes = builder.MaxRequestBodyBytes;
        Forms               = builder.Forms.Copy();
        Pipeline            = pipeline;
    }

    /// <summary>Finds the last registered metadata object assignable to the requested type.</summary>
    public T? GetMetadata<T>() where T : class => Metadata.OfType<T>().LastOrDefault();
}

/// <summary>Configures a controller endpoint during startup, before its immutable description is created.</summary>
public sealed class EndpointBuilder
{
    /// <summary>The registered HTTP method.</summary>
    public string Method { get; }

    /// <summary>The complete route template.</summary>
    public string Template { get; }

    /// <summary>The optional route name.</summary>
    public string? Name { get; }

    /// <summary>The controller service type.</summary>
    public Type ControllerType { get; }

    /// <summary>The request deadline measured from request arrival; null retains the application default.</summary>
    public TimeSpan? RequestTimeout { get; set; }

    /// <summary>The body limit. Defaults to the server ceiling and cannot exceed it.</summary>
    public long MaxRequestBodyBytes { get; set; }

    /// <summary>Endpoint form limits, initialized from application defaults and copied at startup.</summary>
    public FormOptions Forms { get; }

    /// <summary>Application metadata. Later entries take precedence in GetMetadata.</summary>
    public IList<object> Metadata { get; } = new List<object>();

    /// <summary>Middleware combined with controller/action attributes and ordered by priority.</summary>
    public IList<MiddlewareAttribute> Middleware { get; } = new List<MiddlewareAttribute>();

    internal EndpointBuilder(string method, string template, string? name, Type controllerType, ServalOptions options)
    {
        Method              = method;
        Template            = template;
        Name                = name;
        ControllerType      = controllerType;
        MaxRequestBodyBytes = options.MaxRequestBodyBytes;
        Forms               = options.Forms.Copy();
    }

    internal void Validate(ServalOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxRequestBodyBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxRequestBodyBytes, options.MaxRequestBodyBytes);

        if (RequestTimeout is {} timeout        &&
            timeout != Timeout.InfiniteTimeSpan &&
            (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > uint.MaxValue - 1))
        {
            throw new ArgumentOutOfRangeException(nameof(RequestTimeout));
        }

        if (Metadata.Any(static (object? item) => item is null) || Middleware.Any(static (MiddlewareAttribute? item) => item is null))
        {
            throw new ArgumentException("Endpoint metadata and middleware cannot contain null entries.");
        }

        Forms.Validate();
    }
}
