#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Text.Json;

namespace Serval.Savanna;

public sealed partial class SavannaWriter
{
    private static readonly byte[] AttributeQuote = "\""u8.ToArray();
    private static readonly byte[] JsonStart      = "<script type=\"application/json\" id=\""u8.ToArray();
    private static readonly byte[] JsonValueStart = "\">"u8.ToArray();
    private static readonly byte[] JsonEnd        = "</script>"u8.ToArray();
    private static readonly byte[] JsonLessThan   = "\\u003C"u8.ToArray();

    /// <summary>Writes a valueless HTML attribute only when the value is true.</summary>
    /// <param name="name">The validated attribute name or name to validate.</param>
    /// <param name="value">True to emit the attribute; false or null omits it.</param>
    /// <param name="context">The active render context.</param>
    /// <returns>An operation covering any emitted bytes.</returns>
    public ValueTask WriteBooleanAttributeAsync(string name, bool? value, SavannaRenderContext context)
        => WriteBooleanAttributeAsync(new SavannaAttributeName(name), value, context);

    /// <summary>Writes an HTML-escaped attribute value, omitting null values.</summary>
    /// <typeparam name="T">The attribute value type.</typeparam>
    /// <param name="name">The validated attribute name or name to validate.</param>
    /// <param name="value">The attribute value to format and encode.</param>
    /// <param name="context">The formatting and cancellation context.</param>
    /// <returns>An operation covering the complete attribute.</returns>
    /// <remarks>
    ///     Boolean values are lowercase text, except a boolean hidden attribute uses HTML presence/absence semantics.
    ///     Attribute-name validation does not make event handlers or other executable attributes safe.
    /// </remarks>
    public ValueTask WriteAttributeAsync<T>(string name, T value, SavannaRenderContext context)
        => WriteAttributeAsync(new SavannaAttributeName(name), value, context);

    /// <summary>Writes a quoted, HTML-escaped URL attribute after checking the optional policy.</summary>
    /// <param name="name">The validated attribute name or name to validate.</param>
    /// <param name="value">The URL value; a null underlying value omits the attribute.</param>
    /// <param name="context">The render context containing the optional URL policy.</param>
    /// <returns>An operation covering the complete attribute.</returns>
    /// <remarks>
    ///     String and Uri inputs do not run SavannaUrl.From validation. The policy is checked before any part of the attribute
    ///     is emitted.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The application URL policy rejects the URL.</exception>
    public ValueTask WriteUrlAttributeAsync(string name, SavannaUrl value, SavannaRenderContext context)
        => WriteUrlAttributeAsync(new SavannaAttributeName(name), value.Value, context);

    /// <summary>Writes a quoted, HTML-escaped URL attribute after checking the optional policy.</summary>
    /// <param name="name">The validated attribute name or name to validate.</param>
    /// <param name="value">The URL value; a null underlying value omits the attribute.</param>
    /// <param name="context">The render context containing the optional URL policy.</param>
    /// <returns>An operation covering the complete attribute.</returns>
    /// <remarks>
    ///     String and Uri inputs do not run SavannaUrl.From validation. The policy is checked before any part of the attribute
    ///     is emitted.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The application URL policy rejects the URL.</exception>
    public ValueTask WriteUrlAttributeAsync(string name, Uri? value, SavannaRenderContext context)
        => WriteUrlAttributeAsync(new SavannaAttributeName(name), value?.OriginalString, context);

    /// <summary>Writes a quoted, HTML-escaped URL attribute after checking the optional policy.</summary>
    /// <param name="name">The validated attribute name or name to validate.</param>
    /// <param name="value">The URL value; a null underlying value omits the attribute.</param>
    /// <param name="context">The render context containing the optional URL policy.</param>
    /// <returns>An operation covering the complete attribute.</returns>
    /// <remarks>
    ///     String and Uri inputs do not run SavannaUrl.From validation. The policy is checked before any part of the attribute
    ///     is emitted.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The application URL policy rejects the URL.</exception>
    public ValueTask WriteUrlAttributeAsync(string name, string? value, SavannaRenderContext context)
        => WriteUrlAttributeAsync(new SavannaAttributeName(name), value, context);

    /// <summary>Writes a valueless HTML attribute only when the value is true.</summary>
    /// <param name="name">The validated attribute name or name to validate.</param>
    /// <param name="value">True to emit the attribute; false or null omits it.</param>
    /// <param name="context">The active render context.</param>
    /// <returns>An operation covering any emitted bytes.</returns>
    public ValueTask WriteBooleanAttributeAsync(SavannaAttributeName name, bool? value, SavannaRenderContext context)
    {
        CheckAttributeWrite(context);
        return value == true ? WriteLiteralAsync(name.BooleanPrefix, context.CancellationToken) : ValueTask.CompletedTask;
    }

    /// <summary>Writes an HTML-escaped attribute value, omitting null values.</summary>
    /// <typeparam name="T">The attribute value type.</typeparam>
    /// <param name="name">The validated attribute name or name to validate.</param>
    /// <param name="value">The attribute value to format and encode.</param>
    /// <param name="context">The formatting and cancellation context.</param>
    /// <returns>An operation covering the complete attribute.</returns>
    /// <remarks>
    ///     Boolean values are lowercase text, except a boolean hidden attribute uses HTML presence/absence semantics.
    ///     Attribute-name validation does not make event handlers or other executable attributes safe.
    /// </remarks>
    public async ValueTask WriteAttributeAsync<T>(SavannaAttributeName name, T value, SavannaRenderContext context)
    {
        CheckAttributeWrite(context);
        if (value is null)
        {
            return;
        }

        if (name.IsHidden && value is bool hidden)
        {
            await WriteBooleanAttributeAsync(name, hidden, context);
            return;
        }

        await WriteLiteralAsync(name.Prefix, context.CancellationToken);
        if (value is bool boolean)
        {
            await WriteEncodedAsync(boolean ? "true" : "false", context.CancellationToken);
        }
        else
        {
            await WriteValueAsync(value, context);
        }

        await WriteLiteralAsync(AttributeQuote, context.CancellationToken);
    }

    /// <summary>Writes a quoted, HTML-escaped URL attribute after checking the optional policy.</summary>
    /// <param name="name">The validated attribute name or name to validate.</param>
    /// <param name="value">The URL value; a null underlying value omits the attribute.</param>
    /// <param name="context">The render context containing the optional URL policy.</param>
    /// <returns>An operation covering the complete attribute.</returns>
    /// <remarks>
    ///     String and Uri inputs do not run SavannaUrl.From validation. The policy is checked before any part of the attribute
    ///     is emitted.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The application URL policy rejects the URL.</exception>
    public ValueTask WriteUrlAttributeAsync(SavannaAttributeName name, SavannaUrl value, SavannaRenderContext context)
        => WriteUrlAttributeAsync(name, value.Value, context);

    /// <summary>Writes a quoted, HTML-escaped URL attribute after checking the optional policy.</summary>
    /// <param name="name">The validated attribute name or name to validate.</param>
    /// <param name="value">The URL value; a null underlying value omits the attribute.</param>
    /// <param name="context">The render context containing the optional URL policy.</param>
    /// <returns>An operation covering the complete attribute.</returns>
    /// <remarks>
    ///     String and Uri inputs do not run SavannaUrl.From validation. The policy is checked before any part of the attribute
    ///     is emitted.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The application URL policy rejects the URL.</exception>
    public ValueTask WriteUrlAttributeAsync(SavannaAttributeName name, Uri? value, SavannaRenderContext context)
        => WriteUrlAttributeAsync(name, value?.OriginalString, context);

    /// <summary>Writes a quoted, HTML-escaped URL attribute after checking the optional policy.</summary>
    /// <param name="name">The validated attribute name or name to validate.</param>
    /// <param name="value">The URL value; a null underlying value omits the attribute.</param>
    /// <param name="context">The render context containing the optional URL policy.</param>
    /// <returns>An operation covering the complete attribute.</returns>
    /// <remarks>
    ///     String and Uri inputs do not run SavannaUrl.From validation. The policy is checked before any part of the attribute
    ///     is emitted.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The application URL policy rejects the URL.</exception>
    public async ValueTask WriteUrlAttributeAsync(SavannaAttributeName name, string? value, SavannaRenderContext context)
    {
        CheckAttributeWrite(context);
        if (value is null)
        {
            return;
        }

        // Check before emitting any part of the attribute.
        if (context.UrlPolicy is { } policy && !policy.IsAllowed(value))
        {
            throw new InvalidOperationException("The application URL policy rejected a template URL.");
        }

        await WriteLiteralAsync(name.Prefix, context.CancellationToken);
        await WriteEncodedAsync(value, context.CancellationToken);
        await WriteLiteralAsync(AttributeQuote, context.CancellationToken);
    }

    /// <summary>Writes a non-executable application/json script element containing serialized data.</summary>
    /// <typeparam name="T">The serialized value type.</typeparam>
    /// <param name="id">The element identifier, which is HTML-escaped.</param>
    /// <param name="value">The value serialized using the default JSON options.</param>
    /// <param name="context">The active render context.</param>
    /// <returns>An operation covering the complete script element.</returns>
    /// <remarks>
    ///     Serialization finishes before output starts. Less-than characters are escaped even when a converter writes raw
    ///     JSON, preventing a closing script tag from entering the output.
    /// </remarks>
    public async ValueTask WriteJsonAsync<T>(string id, T value, SavannaRenderContext context)
    {
        CheckAttributeWrite(context);
        // The default encoder escapes HTML-sensitive characters, including '<'.
        // Serialize first so a failing converter does not leave an open script tag.
        var json = JsonSerializer.SerializeToUtf8Bytes(value);
        await WriteLiteralAsync(JsonStart, context.CancellationToken);
        await WriteEncodedAsync(id, context.CancellationToken);
        await WriteLiteralAsync(JsonValueStart, context.CancellationToken);
        // A custom converter can use WriteRawValue and bypass the JSON encoder.
        // Keep even that output from terminating the HTML script element.
        var offset = 0;
        while (offset < json.Length)
        {
            var relative = json.AsSpan(offset).IndexOf((byte)'<');
            if (relative < 0)
            {
                await WriteLiteralAsync(json.AsMemory(offset), context.CancellationToken);
                break;
            }

            await WriteLiteralAsync(json.AsMemory(offset, relative), context.CancellationToken);
            await WriteLiteralAsync(JsonLessThan, context.CancellationToken);
            offset += relative + 1;
        }

        await WriteLiteralAsync(JsonEnd, context.CancellationToken);
    }

    private void CheckAttributeWrite(SavannaRenderContext context)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        context.CancellationToken.ThrowIfCancellationRequested();
    }
}
