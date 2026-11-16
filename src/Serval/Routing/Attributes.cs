#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Serval.Http.Constants;

namespace Serval.Routing;

/// <summary>Marks a public top-level controller for generated route registration.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class ControllerAttribute : Attribute;

/// <summary>Sets a controller's route prefix.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class RouteAttribute : Attribute
{
    /// <summary>The controller-level prefix applied to action routes.</summary>
    public string Template { get; }

    /// <summary>Sets the route prefix for a controller.</summary>
    /// <param name="template">The prefix combined with each action's route template.</param>
    public RouteAttribute(string template)
    {
        Template = template;
    }
}

/// <summary>Registers a controller action for an HTTP method and route.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
public class HttpRouteAttribute : Attribute
{
    /// <summary>The HTTP method handled by the action.</summary>
    public string Method { get; }

    /// <summary>
    ///     The action template combined with its controller's prefix. Start with ~/ to bypass that prefix.
    /// </summary>
    public string Template { get; }

    /// <summary>
    ///     An optional unique, case-sensitive route name. Generated registrations default to Controller.Action, adding a
    ///     numeric suffix for additional routes.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>Sets the HTTP method and route template for an action.</summary>
    /// <param name="method">The HTTP method to match.</param>
    /// <param name="template">
    ///     The action route template, relative to the controller prefix; defaults to an empty suffix.
    /// </param>
    public HttpRouteAttribute(string method, string template = "")
    {
        Method   = method;
        Template = template;
    }
}

/// <summary>Maps an action to the GET method.</summary>
public sealed class GetAttribute : HttpRouteAttribute
{
    /// <summary>Sets the route template for a GET action.</summary>
    /// <param name="template">
    ///     The action route template, relative to its controller prefix; defaults to an empty suffix.
    /// </param>
    public GetAttribute(string template = "") : base(HttpMethods.Get, template) {}
}

/// <summary>Maps an action to the safe, idempotent QUERY method.</summary>
/// <remarks>
///     QUERY actions must not request changes to the target resource. Serval requires a valid Content-Type;
///     actions must validate the content using a parser for their supported query format, such as JSON body binding.
///     Advertise supported formats with <see cref="HeaderNames.AcceptQuery" /> when needed.
///     Cross-origin browser requests require a CORS preflight that allows QUERY.
///     Any response cache must include the request content and its metadata in the cache key.
/// </remarks>
public sealed class QueryAttribute : HttpRouteAttribute
{
    /// <summary>Sets the route template for a QUERY action.</summary>
    /// <param name="template">The action route template, relative to its controller prefix; defaults to an empty suffix.</param>
    public QueryAttribute(string template = "") : base(HttpMethods.Query, template) {}
}

/// <summary>Maps an action to the POST method.</summary>
public sealed class PostAttribute : HttpRouteAttribute
{
    /// <summary>Sets the route template for a POST action.</summary>
    /// <param name="template">
    ///     The action route template, relative to its controller prefix; defaults to an empty suffix.
    /// </param>
    public PostAttribute(string template = "") : base(HttpMethods.Post, template) {}
}

/// <summary>Maps an action to the PUT method.</summary>
public sealed class PutAttribute : HttpRouteAttribute
{
    /// <summary>Sets the route template for a PUT action.</summary>
    /// <param name="template">
    ///     The action route template, relative to its controller prefix; defaults to an empty suffix.
    /// </param>
    public PutAttribute(string template = "") : base(HttpMethods.Put, template) {}
}

/// <summary>Maps an action to the PATCH method.</summary>
public sealed class PatchAttribute : HttpRouteAttribute
{
    /// <summary>Sets the route template for a PATCH action.</summary>
    /// <param name="template">
    ///     The action route template, relative to its controller prefix; defaults to an empty suffix.
    /// </param>
    public PatchAttribute(string template = "") : base(HttpMethods.Patch, template) {}
}

/// <summary>Maps an action to the DELETE method.</summary>
public sealed class DeleteAttribute : HttpRouteAttribute
{
    /// <summary>Sets the route template for a DELETE action.</summary>
    /// <param name="template">
    ///     The action route template, relative to its controller prefix; defaults to an empty suffix.
    /// </param>
    public DeleteAttribute(string template = "") : base(HttpMethods.Delete, template) {}
}

/// <summary>Maps an action to the HEAD method.</summary>
public sealed class HeadAttribute : HttpRouteAttribute
{
    /// <summary>Sets the route template for a HEAD action.</summary>
    /// <param name="template">
    ///     The action route template, relative to its controller prefix; defaults to an empty suffix.
    /// </param>
    public HeadAttribute(string template = "") : base(HttpMethods.Head, template) {}
}

/// <summary>Maps an action to the OPTIONS method.</summary>
public sealed class OptionsAttribute : HttpRouteAttribute
{
    /// <summary>Sets the route template for an OPTIONS action.</summary>
    /// <param name="template">
    ///     The action route template, relative to its controller prefix; defaults to an empty suffix.
    /// </param>
    public OptionsAttribute(string template = "") : base(HttpMethods.Options, template) {}
}
