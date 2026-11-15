#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Text.Json.Serialization;

namespace Serval.SignedUrls;

// Only used for serialization; incoming JSON uses SignedUrlInput below.
internal sealed class SignedUrlPayload
{
    public required int                          Version     { get; init; }
    public required string                       Path        { get; init; }
    public required string?                      RouteName   { get; init; }
    public required Dictionary<string, string?>  RouteValues { get; init; }
    public required Dictionary<string, string[]> Query       { get; init; }
    public required DateTimeOffset?              ExpiresAt   { get; init; }
}

// Required JSON properties can still contain null, including inside collections.
// Keep these annotations honest so static analysis preserves the boundary checks.
internal sealed class SignedUrlInput
{
    public required int                             Version     { get; init; }
    public required string?                         Path        { get; init; }
    public required string?                         RouteName   { get; init; }
    public required Dictionary<string, string?>?    RouteValues { get; init; }
    public required Dictionary<string, string?[]?>? Query       { get; init; }
    public required DateTimeOffset?                 ExpiresAt   { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, AllowDuplicateProperties = false)]
[JsonSerializable(typeof(SignedUrlPayload))]
[JsonSerializable(typeof(SignedUrlInput))]
internal sealed partial class SignedUrlJsonContext : JsonSerializerContext;
