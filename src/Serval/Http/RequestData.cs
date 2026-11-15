#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Serval.Http.Constants;

namespace Serval.Http;

/// <summary>Cached form fields or top-level JSON properties, plus multipart files.</summary>
/// <remarks>Names ignore case. Scalar reads reject repeated fields. Values and files belong to the request.</remarks>
public sealed class RequestData
{
    /// <summary>The submitted field or JSON property names, excluding file parts.</summary>
    public IEnumerable<string> Names => _fields is not null ? _fields.Keys : _properties!.Keys;

    /// <summary>Uploaded files in submission order. Empty for JSON and URL-encoded bodies.</summary>
    public IReadOnlyList<UploadedFile> Files { get; }

    private readonly JsonDocument? _document;

    private readonly Dictionary<string, string[]>?    _fields;
    private readonly Dictionary<string, JsonElement>? _properties;

    internal RequestData(Dictionary<string, string[]> fields, List<UploadedFile> files)
    {
        _fields = fields;
        Files   = files.AsReadOnly();
    }

    internal RequestData(JsonDocument document, Dictionary<string, JsonElement> properties)
    {
        _document   = document;
        _properties = properties;
        Files       = Array.Empty<UploadedFile>();
    }

    /// <summary>Whether a field or JSON property is present, including an explicit JSON null.</summary>
    public bool ContainsKey(string name) => _fields?.ContainsKey(name) ?? _properties!.ContainsKey(name);

    /// <summary>
    ///     Gets exactly one required scalar using invariant culture. Missing, null, repeated, or invalid values return
    ///     400.
    /// </summary>
    public T Get<T>(string name) where T : IParsable<T>
        => TryGet<T>(name, out var value) ? value : throw Invalid(name);

    /// <summary>Gets a scalar, using the fallback only when the field is absent. Invalid submitted values return 400.</summary>
    public T Get<T>(string name, T fallback) where T : IParsable<T> => ContainsKey(name) ? Get<T>(name) : fallback;

    /// <summary>Tries to read exactly one non-null scalar. Returns false for absent, repeated, or invalid values.</summary>
    public bool TryGet<T>(string name, [MaybeNullWhen(false)] out T value) where T : IParsable<T>
    {
        value = default;
        if (!TryGetStrings(name, out var values) || values.Length != 1 || values[0] is not { } text)
        {
            return false;
        }

        return T.TryParse(text, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>Gets repeated form values or a JSON array as typed scalars. Missing fields return an empty array.</summary>
    public T[] GetValues<T>(string name) where T : IParsable<T>
    {
        if (!ContainsKey(name))
        {
            return [];
        }

        if (!TryGetStrings(name, out var values))
        {
            throw Invalid(name);
        }

        var result = new T[values.Length];
        for (var index = 0; index < values.Length; index++)
        {
            if (values[index] is not { } text || !T.TryParse(text, CultureInfo.InvariantCulture, out var value))
            {
                throw Invalid(name);
            }

            result[index] = value;
        }

        return result;
    }

    /// <summary>Gets a required enum scalar. Names ignore case; numeric values and flags follow Enum.TryParse.</summary>
    public T GetEnum<T>(string name) where T : struct, Enum
        => TryGetStrings(name, out var values) && values.Length == 1 && Enum.TryParse<T>(values[0], true, out var value) ? value : throw Invalid(name);

    /// <summary>Deserializes a required top-level JSON property, including nested objects and arrays, using explicit metadata.</summary>
    /// <remarks>Available for JSON bodies. The returned object's members still need application validation.</remarks>
    public T Get<T>(string name, JsonTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        if (_properties is null || !_properties.TryGetValue(name, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            throw Invalid(name);
        }

        try
        {
            return element.Deserialize(typeInfo) ?? throw Invalid(name);
        }
        catch (JsonException exception)
        {
            throw new HttpErrorException(StatusCodes.BadRequest, $"POST property '{name}' is invalid.", innerException: exception);
        }
    }

    /// <summary>Gets a single uploaded file, or null when absent. Repeated files with that name return 400.</summary>
    public UploadedFile? GetFile(string name)
    {
        var files = GetFiles(name);
        return files.Count switch { 0 => null, 1 => files[0], _ => throw Invalid(name) };
    }

    /// <summary>Gets every uploaded file with the specified name, preserving submission order.</summary>
    public IReadOnlyList<UploadedFile> GetFiles(string name)
        => Files.Where(file => string.Equals(file.Name, name, StringComparison.OrdinalIgnoreCase)).ToArray();

    internal void Dispose() => _document?.Dispose();

    internal IReadOnlyList<string> FormValues(string name)
        => _fields is not null && _fields.TryGetValue(name, out var values) ? values : [];

    private bool TryGetStrings(string name, out string?[] values)
    {
        values = [];
        if (_fields is not null)
        {
            if (!_fields.TryGetValue(name, out var fields))
            {
                return false;
            }

            values = fields;
            return true;
        }

        if (!_properties!.TryGetValue(name, out var element))
        {
            return false;
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            values = [ScalarText(element)];
            return element.ValueKind is not (JsonValueKind.Object or JsonValueKind.Undefined);
        }

        values = new string?[element.GetArrayLength()];
        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                return false;
            }

            values[index++] = ScalarText(item);
        }

        return true;
    }

    private static string? ScalarText(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True   => "true",
        JsonValueKind.False  => "false",
        _                    => null,
    };

    private static HttpErrorException Invalid(string name) => new(StatusCodes.BadRequest, $"POST field '{name}' is missing, repeated, null, or invalid.");
}
