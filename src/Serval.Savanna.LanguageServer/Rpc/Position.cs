#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.CodeAnalysis.Text;
using System.Text.Json;

namespace Serval.Savanna.LanguageServer.Rpc;

public readonly record struct Position(int Line, int Character)
{
    public int Offset(SourceText text)
    {
        if (Line < 0 || Line >= text.Lines.Count || Character < 0 || Character > text.Lines[Line].Span.Length)
        {
            throw new ArgumentOutOfRangeException(null, "Position is outside the document.");
        }

        return text.Lines[Line].Start + Character;
    }

    public static Position Read(JsonElement value) => new(value.GetProperty("line").GetInt32(), value.GetProperty("character").GetInt32());

    public static object Range(SourceText text, TextSpan span)
    {
        var start = text.Lines.GetLinePosition(Math.Clamp(span.Start, 0, text.Length));
        var end   = text.Lines.GetLinePosition(Math.Clamp(span.End, 0, text.Length));
        return new { start = new { line = start.Line, character = start.Character }, end = new { line = end.Line, character = end.Character } };
    }
}
