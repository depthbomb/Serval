#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Buffers;
using System.Buffers.Text;

namespace Serval.SignedUrls;

internal static class SignedUrlEncoding
{
    private const string Base64Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";

    private static readonly SearchValues<char> Base64Characters = SearchValues.Create(Base64Alphabet);

    internal static bool TryParseUrl(string? url, int maxLength, bool relativeOnly, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrEmpty(url))
        {
            return false;
        }

        var relative = url.StartsWith('/');
        if (relative && (url.Length > maxLength || url.StartsWith("//", StringComparison.Ordinal)) || relativeOnly && !relative || !HasValidCharacters(url))
        {
            return false;
        }

        var candidate = relative ? "https://signed-url.invalid" + url : url;
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var parsed) || !IsValidUri(parsed, maxLength))
        {
            return false;
        }

        uri = parsed;
        return true;
    }

    internal static bool IsValidUri(Uri uri, int maxLength)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme is not "http" and not "https" || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
        {
            return false;
        }

        var path  = uri.AbsolutePath;
        var query = uri.Query;
        // Dot-segment normalization can turn a single-slash input into a network-path reference.
        return path.StartsWith('/')                             &&
               !path.StartsWith("//", StringComparison.Ordinal) &&
               path.Length + query.Length <= maxLength          &&
               HasValidCharacters(path)                         &&
               HasValidCharacters(query);
    }

    internal static Dictionary<string, string[]> ParseQuery(string query)
    {
        // Match Request.Query: names ignore case, '+' is a space, and repeated values keep their order.
        var                               parsed   = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, List<string>>? repeated = null;
        var                               names    = parsed.GetAlternateLookup<ReadOnlySpan<char>>();
        var                               content  = query.AsSpan(query.StartsWith('?') ? 1 : 0);
        foreach (var range in content.Split('&'))
        {
            var part = content[range];
            if (part.IsEmpty)
            {
                continue;
            }

            var separator = part.IndexOf('=');
            var name      = separator < 0 ? part : part[..separator];
            var value     = separator < 0 ? "" : Decode(part[(separator + 1)..]);
            if (name.ContainsAny('%', '+'))
            {
                name = Decode(name);
            }

            if (repeated is not null && repeated.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(name, out var values))
            {
                values.Add(value);
            }
            else if (names.TryGetValue(name, out var key, out var first))
            {
                repeated ??= new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                repeated.Add(key, [first[0], value]);
            }
            else
            {
                parsed.Add(name.ToString(), [value]);
            }
        }

        if (repeated is not null)
        {
            foreach (var pair in repeated)
            {
                parsed[pair.Key] = [.. pair.Value];
            }
        }

        return parsed;
    }

    internal static string Encode(ReadOnlySpan<byte> bytes) => Base64Url.EncodeToString(bytes);

    internal static bool IsCanonicalToken(ReadOnlySpan<char> text)
    {
        var remainder = text.Length & 3;
        if (text.IsEmpty || remainder == 1 || text.IndexOfAnyExcept(Base64Characters) >= 0)
        {
            return false;
        }

        // Unused bits must be zero; decoders otherwise accept multiple spellings of the same bytes.
        return remainder == 0 || (Base64Alphabet.IndexOf(text[^1]) & (remainder == 2 ? 15 : 3)) == 0;
    }

    internal static bool TryDecodeToken(ReadOnlySpan<char> text, Span<byte> destination)
        => IsCanonicalToken(text) && Base64Url.TryDecodeFromChars(text, destination, out var written) && written == destination.Length;

    private static bool HasValidCharacters(ReadOnlySpan<char> text)
    {
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            switch (character)
            {
                case <= '\u001f' or >= '\u007f' and <= '\u009f' or '#' or '\\':
                case '%' when index + 2 >= text.Length || !Uri.IsHexDigit(text[index + 1]) || !Uri.IsHexDigit(text[index + 2]):
                    return false;
            }
        }

        return true;
    }

    private static string Decode(ReadOnlySpan<char> value)
    {
        if (value.Contains('+'))
        {
            value = value.ToString().Replace('+', ' ');
        }

        return Uri.UnescapeDataString(value);
    }
}
