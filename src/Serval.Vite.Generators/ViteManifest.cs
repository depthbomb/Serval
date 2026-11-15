#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Text.Json;

namespace Serval.Vite.Generators;

internal sealed class ManifestChunk
{
    public string   File           { get; }
    public bool     IsEntry        { get; }
    public string[] Imports        { get; }
    public string[] DynamicImports { get; }
    public string[] Css            { get; }

    public ManifestChunk(string file, bool isEntry, string[] imports, string[] dynamicImports, string[] css)
    {
        File           = file;
        IsEntry        = isEntry;
        Imports        = imports;
        DynamicImports = dynamicImports;
        Css            = css;
    }
}

internal sealed class ManifestException : Exception
{
    public ManifestException(string message) : base(message) { }
}

internal static class ViteManifest
{
    public static Dictionary<string, ManifestChunk> Parse(string json, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new ManifestException("Use a client build manifest: the root must be an object keyed by source path.");
        }

        var chunks = new Dictionary<string, ManifestChunk>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key   = ReadPropertyName(property);
            var value = property.Value;
            if (key.Length == 0 || chunks.ContainsKey(key))
            {
                throw new ManifestException($"Manifest keys must be nonempty and unique: '{key}'.");
            }

            if (value.ValueKind != JsonValueKind.Object)
            {
                throw new ManifestException($"Chunk '{key}' must be an object. SSR manifests are not supported.");
            }

            var     fields         = new HashSet<string>(StringComparer.Ordinal);
            string? file           = null;
            var     isEntry        = false;
            var     imports        = Array.Empty<string>();
            var     dynamicImports = Array.Empty<string>();
            var     css            = Array.Empty<string>();
            foreach (var field in value.EnumerateObject())
            {
                var name = ReadPropertyName(field);
                if (!fields.Add(name))
                {
                    throw new ManifestException($"Chunk '{key}' repeats the '{name}' field.");
                }

                switch (name)
                {
                    case "file":
                        file = ReadString(field.Value, key, name, true);
                        break;
                    case "src":
                    case "name":
                        ReadString(field.Value, key, name, false);
                        break;
                    case "isEntry":
                        isEntry = ReadBoolean(field.Value, key, name);
                        break;
                    case "isDynamicEntry":
                        ReadBoolean(field.Value, key, name);
                        break;
                    case "imports":
                        imports = ReadList(field.Value, key, name, cancellationToken);
                        break;
                    case "dynamicImports":
                        dynamicImports = ReadList(field.Value, key, name, cancellationToken);
                        break;
                    case "css":
                        css = ReadList(field.Value, key, name, cancellationToken);
                        break;
                    case "assets":
                    case "names":
                        ReadList(field.Value, key, name, cancellationToken);
                        break;
                }
            }

            if (file is null)
            {
                throw new ManifestException($"Chunk '{key}' needs a nonempty string 'file'.");
            }

            chunks.Add(key, new ManifestChunk(file, isEntry, imports, dynamicImports, css));
        }

        foreach (var pair in chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Only imports are manifest keys. CSS and assets lists contain output filenames.
            foreach (var reference in pair.Value.Imports.Concat(pair.Value.DynamicImports))
            {
                if (!chunks.ContainsKey(reference))
                {
                    throw new ManifestException($"Chunk '{pair.Key}' references missing chunk '{reference}'.");
                }
            }
        }

        return chunks;
    }

    private static string ReadString(JsonElement value, string key, string field, bool nonempty)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new ManifestException($"Chunk '{key}': '{field}' must be a {(nonempty ? "nonempty " : "")}string.");
        }

        string text;
        try
        {
            text = value.GetString()!;
        }
        catch (InvalidOperationException)
        {
            // JSON parsing can succeed before an invalid escaped surrogate is decoded by GetString.
            throw new ManifestException($"Chunk '{key}': '{field}' contains invalid Unicode text.");
        }

        if (nonempty && text.Length == 0)
        {
            throw new ManifestException($"Chunk '{key}': '{field}' must be a nonempty string.");
        }

        return text;
    }

    private static string ReadPropertyName(JsonProperty property)
    {
        try
        {
            return property.Name;
        }
        catch (InvalidOperationException)
        {
            throw new ManifestException("A manifest property name contains invalid Unicode text.");
        }
    }

    private static bool ReadBoolean(JsonElement value, string key, string field)
    {
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new ManifestException($"Chunk '{key}': '{field}' must be a boolean.");
        }

        return value.GetBoolean();
    }

    private static string[] ReadList(JsonElement value, string key, string field, CancellationToken cancellationToken)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new ManifestException($"Chunk '{key}': '{field}' must be an array of nonempty strings.");
        }

        var result = new string[value.GetArrayLength()];
        var index  = 0;
        foreach (var item in value.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            result[index++] = ReadString(item, key, field, true);
        }

        return result;
    }
}
