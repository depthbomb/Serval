#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

namespace Serval.Configuration;

internal static class DotEnvKeyMappings
{
    internal static Dictionary<string, string> Create(IEnumerable<KeyValuePair<string, string>> mappings)
    {
        var result  = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (alias, path) in mappings)
        {
            if (!IsValidKey(alias) || alias.Contains(':') || alias.Contains("__", StringComparison.Ordinal))
            {
                throw new ArgumentException("Mapping names must be flat dotenv keys.", nameof(mappings));
            }

            if (!IsValidKey(path))
            {
                throw new ArgumentException("Mapping destinations must be nonempty configuration paths.", nameof(mappings));
            }

            var destination = Normalize(path);
            if (!aliases.Add(alias) || !result.TryAdd(destination, alias))
            {
                throw new ArgumentException("Each mapping name and destination must be unique, ignoring case.", nameof(mappings));
            }
        }

        if (result.Keys.Any(aliases.Contains))
        {
            throw new ArgumentException("Mapping destinations cannot also be mapping names; chained mappings are not supported.", nameof(mappings));
        }

        return result;
    }

    internal static string Normalize(string key) => key.Replace("__", ":", StringComparison.Ordinal);

    internal static bool IsValidKey(ReadOnlySpan<char> key)
    {
        if (key.IsEmpty || !(char.IsAsciiLetter(key[0]) || key[0] == '_'))
        {
            return false;
        }

        var hasSectionCharacter = false;
        for (var index = 0; index < key.Length; index++)
        {
            var character = key[index];
            if (!(char.IsAsciiLetterOrDigit(character) || character is '_' or '.' or '-' or ':'))
            {
                return false;
            }

            var isSeparator = character == ':';
            if (character == '_' && index + 1 < key.Length && key[index + 1] == '_')
            {
                isSeparator = true;
                index++;
            }

            if (isSeparator && !hasSectionCharacter)
            {
                return false;
            }

            hasSectionCharacter = !isSeparator;
        }

        return hasSectionCharacter;
    }
}
