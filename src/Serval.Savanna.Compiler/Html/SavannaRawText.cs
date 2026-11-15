#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

namespace Serval.Savanna.Compiler.Html;

// HTML script comments can enter a double-escaped state where </script> is still text.
internal sealed class SavannaRawText
{
    private bool _doubleEscaped;
    private bool _escaped;

    public void Reset()
    {
        _escaped       = false;
        _doubleEscaped = false;
    }

    public bool IsClosingTag(string text, int index, string name)
    {
        if (name == "plaintext")
        {
            return false;
        }

        if (name == "script")
        {
            if (Matches(text, index, "-->"))
            {
                Reset();
            }
            else if (Matches(text, index, "<!--"))
            {
                _escaped = true;
            }
            else if (_escaped && IsTag(text, index, "<script"))
            {
                _doubleEscaped = true;
            }
            else if (_doubleEscaped && IsTag(text, index, "</script"))
            {
                _doubleEscaped = false;
                return false;
            }
        }

        return text[index] == '<' && !_doubleEscaped && IsTag(text, index, "</" + name);
    }

    private static bool IsTag(string text, int index, string prefix)
    {
        var end = index + prefix.Length;
        return end < text.Length && Matches(text, index, prefix) && text[end] is '>' or '/' or ' ' or '\t' or '\r' or '\n' or '\f';
    }

    private static bool Matches(string text, int index, string value)
        => index + value.Length <= text.Length && string.Compare(text, index, value, 0, value.Length, StringComparison.OrdinalIgnoreCase) == 0;
}
