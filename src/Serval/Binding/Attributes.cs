#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

namespace Serval.Binding;

/// <summary>Binds an action parameter to a decoded route value.</summary>
/// <remarks>Optional route values require a nullable parameter or a C# default value.</remarks>
[AttributeUsage(AttributeTargets.Parameter)]
public sealed class FromRouteAttribute : Attribute
{
    /// <summary>The route parameter name, or null to use the action parameter's name.</summary>
    public string? Name { get; }

    /// <summary>Selects the route value to bind.</summary>
    /// <param name="name">The case-insensitive route parameter name, or null to use the action parameter's name.</param>
    public FromRouteAttribute(string? name = null)
    {
        Name = name;
    }
}

/// <summary>Binds an action parameter to a query value.</summary>
/// <remarks>Scalar parameters reject repeated values. Arrays preserve all values in their original order.</remarks>
[AttributeUsage(AttributeTargets.Parameter)]
public sealed class FromQueryAttribute : Attribute
{
    /// <summary>The query name, or null to use the action parameter's name.</summary>
    public string? Name { get; }

    /// <summary>Selects the query value to bind.</summary>
    /// <param name="name">The case-insensitive query name, or null to use the action parameter's name.</param>
    public FromQueryAttribute(string? name = null)
    {
        Name = name;
    }
}

/// <summary>Binds an action parameter to a request header.</summary>
/// <remarks>
///     Scalars require one header value. Arrays preserve separate header values without splitting commas inside them.
/// </remarks>
[AttributeUsage(AttributeTargets.Parameter)]
public sealed class FromHeaderAttribute : Attribute
{
    /// <summary>The header name, or null to use the action parameter's name.</summary>
    public string? Name { get; }

    /// <summary>Selects the header to bind.</summary>
    /// <param name="name">The case-insensitive header name, or null to use the action parameter's name.</param>
    public FromHeaderAttribute(string? name = null)
    {
        Name = name;
    }
}

/// <summary>Deserializes the request's JSON body into an action parameter.</summary>
/// <remarks>
///     Only one body parameter is allowed. A non-null JSON body is required, including for nullable parameters.
///     Uses the configured JSON metadata and the usual request cancellation, content-type checks, and body limit.
/// </remarks>
[AttributeUsage(AttributeTargets.Parameter)]
public sealed class FromBodyAttribute : Attribute;

/// <summary>Binds a scalar, scalar array, UploadedFile, or UploadedFile array from a URL-encoded or multipart form.</summary>
/// <remarks>Multiple form parameters share one parse. Cannot be combined with FromBody on the same action.</remarks>
[AttributeUsage(AttributeTargets.Parameter)]
public sealed class FromFormAttribute : Attribute
{
    /// <summary>The form field name, or null to use the action parameter name.</summary>
    public string? Name { get; }

    /// <summary>Selects the form field to bind.</summary>
    public FromFormAttribute(string? name = null)
    {
        Name = name;
    }
}

/// <summary>Resolves an action parameter from the request's service provider.</summary>
/// <remarks>Nullable parameters allow an unregistered service; other parameters require a registration.</remarks>
[AttributeUsage(AttributeTargets.Parameter)]
public sealed class FromServicesAttribute : Attribute;
