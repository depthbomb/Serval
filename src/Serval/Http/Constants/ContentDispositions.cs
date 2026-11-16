#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

namespace Serval.Http.Constants;

/// <summary>Content-Disposition type tokens; parameters such as filename are supplied separately.</summary>
public static class ContentDispositions
{
    /// <summary>Displays the representation inline when supported.</summary>
    public const string Inline = "inline";

    /// <summary>Offers the representation as a download.</summary>
    public const string Attachment = "attachment";

    /// <summary>Identifies a field or file in a multipart form.</summary>
    public const string FormData = "form-data";
}
