#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Text;

namespace Serval.Configuration;

internal static class DotEnvParser
{
    internal static Dictionary<string, string?> Parse(Stream stream)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        var data       = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var lineNumber = 0;
        while (reader.ReadLine() is { } line)
        {
            lineNumber++;
            var content = line.AsSpan().TrimStart();
            if (content.Length == 0 || content[0] == '#')
            {
                continue;
            }

            if (content.StartsWith("export ", StringComparison.Ordinal) || content.StartsWith("export\t", StringComparison.Ordinal))
            {
                content = content[6..].TrimStart();
            }

            var separator = content.IndexOf('=');
            if (separator < 0)
            {
                throw Error(lineNumber, "Expected a key=value assignment.");
            }

            var keyText = content[..separator].TrimEnd();
            if (!DotEnvKeyMappings.IsValidKey(keyText))
            {
                throw Error(lineNumber, "Invalid configuration key or empty section name.");
            }

            var key = DotEnvKeyMappings.Normalize(keyText.ToString());
            var assignmentLine = lineNumber;
            var value = ReadValue(content[(separator + 1)..].TrimStart(), reader, ref lineNumber);
            if (!data.TryAdd(key, value))
            {
                throw Error(assignmentLine, "Duplicate configuration key (keys ignore case and normalize double underscores).");
            }
        }

        return data;
    }

    private static string ReadValue(ReadOnlySpan<char> text, StreamReader reader, ref int lineNumber)
    {
        if (text.Length == 0)
        {
            return string.Empty;
        }

        var quote = text[0];
        if (quote is not ('\'' or '"'))
        {
            for (var index = 0; index < text.Length; index++)
            {
                if (text[index] == '#' && (index == 0 || char.IsWhiteSpace(text[index - 1])))
                {
                    return text[..index].TrimEnd().ToString();
                }
            }

            return text.TrimEnd().ToString();
        }

        var closingQuote = text[1..].IndexOf(quote) + 1;
        if (closingQuote > 0 && (quote == '\'' || !text[1..closingQuote].Contains('\\')))
        {
            CheckRemainder(text[(closingQuote + 1)..], lineNumber);
            return text[1..closingQuote].ToString();
        }

        var value     = new StringBuilder();
        var startLine = lineNumber;
        var position  = 1;
        while (true)
        {
            for (; position < text.Length; position++)
            {
                var character = text[position];
                if (character == quote)
                {
                    CheckRemainder(text[(position + 1)..], lineNumber);
                    return value.ToString();
                }

                if (quote == '"' && character == '\\' && position + 1 < text.Length)
                {
                    var escaped = text[++position];
                    switch (escaped)
                    {
                        case 'n':
                            value.Append('\n');
                            break;
                        case 'r':
                            value.Append('\r');
                            break;
                        case 't':
                            value.Append('\t');
                            break;
                        case '\\':
                            value.Append('\\');
                            break;
                        case '"':
                            value.Append('"');
                            break;
                        default:
                            value.Append('\\').Append(escaped);
                            break;
                    }
                }
                else
                {
                    value.Append(character);
                }
            }

            var nextLine = reader.ReadLine();
            if (nextLine is null)
            {
                throw Error(startLine, "Unterminated quoted value.");
            }

            value.Append('\n');
            text     = nextLine;
            position = 0;
            lineNumber++;
        }
    }

    private static void CheckRemainder(ReadOnlySpan<char> remainder, int lineNumber)
    {
        remainder = remainder.TrimStart();
        if (!remainder.IsEmpty && remainder[0] != '#')
        {
            throw Error(lineNumber, "Unexpected text after a quoted value.");
        }
    }

    private static FormatException Error(int lineNumber, string message)
        => new($"Invalid dotenv syntax at line {lineNumber}: {message}");
}
