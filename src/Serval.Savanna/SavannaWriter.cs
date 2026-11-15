#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Unicode;

namespace Serval.Savanna;

/// <summary>Buffers UTF-8 output and escapes dynamic HTML values.</summary>
/// <remarks>
///     Use one writer per render and await writes in output order. Flush before disposal; Dispose returns buffers without
///     flushing. The output callback must consume or copy each memory block before its task completes.
/// </remarks>
public sealed partial class SavannaWriter : IDisposable
{
    /// <summary>Whether generated literal output should use its unminified form.</summary>
    public bool PreservesWhitespace { get; private set; }

    private          char[]? _characters;
    private          int     _count;
    private          bool    _disposed;
    private readonly byte[]  _buffer;

    private readonly Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> _write;
    private static readonly SearchValues<char> UnescapedAscii = SearchValues.Create(new string(Enumerable.Range(0, 128)
                                                                                                         .Where(character => !HtmlEncoder.Default.WillEncode(character)).Select(character => (char)character).ToArray()));

    /// <summary>Creates a writer backed by a pooled byte buffer.</summary>
    /// <param name="write">The asynchronous sink; it must not retain the supplied memory after completion.</param>
    /// <param name="bufferSize">The minimum rented buffer size, at least 1,024 bytes; defaults to 16 KiB.</param>
    /// <exception cref="ArgumentNullException">The sink is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The buffer size is below 1,024 bytes.</exception>
    public SavannaWriter(Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> write, int bufferSize = 16 * 1024)
    {
        ArgumentNullException.ThrowIfNull(write);
        ArgumentOutOfRangeException.ThrowIfLessThan(bufferSize, 1024);

        _write  = write;
        _buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
    }

    /// <summary>Clears and returns pooled buffers without flushing pending output.</summary>
    /// <remarks>Flush and await all writes first. Repeated disposal is harmless.</remarks>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ArrayPool<byte>.Shared.Return(_buffer, true);
        if (_characters is not null)
        {
            ArrayPool<char>.Shared.Return(_characters, true);
        }
    }

    /// <summary>Enables whitespace preservation until the returned scope is disposed.</summary>
    /// <returns>A scope that restores the previous mode; dispose nested scopes in reverse order.</returns>
    public WhitespaceScope PreserveWhitespace() => new(this);

    /// <summary>Appends already encoded UTF-8 markup without escaping.</summary>
    /// <param name="bytes">The trusted bytes, kept valid until this operation completes.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>An operation that completes when bytes are buffered or passed to the sink.</returns>
    /// <remarks>Large blocks bypass the buffer after earlier output has been flushed.</remarks>
    public async ValueTask WriteLiteralAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (bytes.Length > _buffer.Length - _count)
        {
            await FlushAsync(cancellationToken);
        }

        if (bytes.Length >= _buffer.Length)
        {
            await _write(bytes, cancellationToken);
            return;
        }

        bytes.Span.CopyTo(_buffer.AsSpan(_count));
        _count += bytes.Length;
    }

    /// <summary>Formats a dynamic value and HTML-escapes the result.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="value">The value; null writes no text.</param>
    /// <param name="context">The format provider and cancellation token for this render.</param>
    /// <returns>An operation that completes when the encoded text has been accepted.</returns>
    /// <remarks>IFormattable values use the context's formatter. Other values use ToString.</remarks>
    public ValueTask WriteValueAsync<T>(T value, SavannaRenderContext context)
    {
        var text = value switch
        {
            null                     => "",
            IFormattable formattable => formattable.ToString(null, context.FormatProvider) ?? "",
            _                        => value.ToString()                                   ?? "",
        };
        return WriteEncodedAsync(text, context.CancellationToken);
    }

    /// <summary>Writes trusted markup as UTF-8 without HTML escaping.</summary>
    /// <param name="value">The explicitly trusted HTML.</param>
    /// <param name="context">The render context and cancellation token.</param>
    /// <returns>An operation that completes when the markup has been accepted.</returns>
    public async ValueTask WriteHtmlAsync(SavannaHtml value, SavannaRenderContext context)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        context.CancellationToken.ThrowIfCancellationRequested();

        var text   = value.Value ?? "";
        var offset = 0;
        while (offset < text.Length)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            // Leave room for one full UTF-8 scalar so a surrogate pair never gets split by a flush.
            if (_buffer.Length - _count < 4)
            {
                await FlushAsync(context.CancellationToken);
            }

            Utf8.FromUtf16(text.AsSpan(offset), _buffer.AsSpan(_count), out var consumed, out var written);
            offset += consumed;
            _count += written;
        }
    }

    /// <summary>Applies the optional URL policy and writes HTML-escaped URL text.</summary>
    /// <param name="value">The URL value; a null underlying value writes no text.</param>
    /// <param name="context">The render context containing the optional URL policy.</param>
    /// <returns>An operation that completes when the encoded URL has been accepted.</returns>
    /// <remarks>
    ///     String and Uri inputs do not run SavannaUrl.From validation. Use SavannaUrl.From or an application policy when scheme
    ///     restrictions are required.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The application URL policy rejects the value.</exception>
    public ValueTask WriteUrlAsync(SavannaUrl value, SavannaRenderContext context)
        => WriteUrlAsync(value.Value, context);

    /// <summary>Applies the optional URL policy and writes HTML-escaped URL text.</summary>
    /// <param name="value">The URL value; a null underlying value writes no text.</param>
    /// <param name="context">The render context containing the optional URL policy.</param>
    /// <returns>An operation that completes when the encoded URL has been accepted.</returns>
    /// <remarks>
    ///     String and Uri inputs do not run SavannaUrl.From validation. Use SavannaUrl.From or an application policy when scheme
    ///     restrictions are required.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The application URL policy rejects the value.</exception>
    public ValueTask WriteUrlAsync(string? value, SavannaRenderContext context)
    {
        if (value is not null && context.UrlPolicy is { } policy && !policy.IsAllowed(value))
        {
            throw new InvalidOperationException("The application URL policy rejected a template URL.");
        }

        return WriteEncodedAsync(value ?? "", context.CancellationToken);
    }

    /// <summary>Applies the optional URL policy and writes HTML-escaped URL text.</summary>
    /// <param name="value">The URL value; a null underlying value writes no text.</param>
    /// <param name="context">The render context containing the optional URL policy.</param>
    /// <returns>An operation that completes when the encoded URL has been accepted.</returns>
    /// <remarks>
    ///     String and Uri inputs do not run SavannaUrl.From validation. Use SavannaUrl.From or an application policy when scheme
    ///     restrictions are required.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The application URL policy rejects the value.</exception>
    public ValueTask WriteUrlAsync(Uri? value, SavannaRenderContext context) => WriteUrlAsync(value?.OriginalString, context);

    /// <summary>HTML-escapes text and buffers the resulting UTF-8 bytes.</summary>
    /// <param name="text">The non-null text to encode.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>An operation that completes when the encoded text has been accepted.</returns>
    /// <exception cref="ArgumentNullException">The text is null.</exception>
    /// <exception cref="ObjectDisposedException">The writer has been disposed.</exception>
    public async ValueTask WriteEncodedAsync(string text, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(text);

        cancellationToken.ThrowIfCancellationRequested();

        // Only bypass HtmlEncoder for ASCII it would leave untouched, including its quote rules.
        if (text.AsSpan().IndexOfAnyExcept(UnescapedAscii) < 0)
        {
            var position = 0;
            while (position < text.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_count == _buffer.Length)
                {
                    await FlushAsync(cancellationToken);
                }

                var length = Math.Min(text.Length - position, _buffer.Length - _count);
                _count   += Encoding.UTF8.GetBytes(text.AsSpan(position, length), _buffer.AsSpan(_count));
                position += length;
            }

            return;
        }

        _characters ??= ArrayPool<char>.Shared.Rent(256);
        var offset = 0;
        while (offset < text.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Encoding at most 256 UTF-16 characters needs fewer than 1,024 UTF-8 bytes.
            if (_buffer.Length - _count < 1024)
            {
                await FlushAsync(cancellationToken);
            }

            var status = HtmlEncoder.Default.Encode(text.AsSpan(offset), _characters.AsSpan(0, 256), out var consumed, out var written);
            if (status is not (OperationStatus.Done or OperationStatus.DestinationTooSmall) || consumed == 0)
            {
                throw new InvalidOperationException("HTML encoding could not make progress.");
            }

            _count += Encoding.UTF8.GetBytes(_characters.AsSpan(0, written), _buffer.AsSpan(_count));
            offset += consumed;
        }
    }

    /// <summary>Sends buffered bytes to the output callback.</summary>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>An operation that completes when the sink has consumed the bytes.</returns>
    /// <remarks>Does not call a transport-level flush. Call this before Dispose to preserve pending output.</remarks>
    public async ValueTask FlushAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (_count > 0)
        {
            await _write(_buffer.AsMemory(0, _count), cancellationToken);
            _count = 0;
        }
    }

    /// <summary>Restores the writer's previous whitespace mode when disposed.</summary>
    public readonly struct WhitespaceScope : IDisposable
    {
        private readonly SavannaWriter? _writer;
        private readonly bool        _previous;

        internal WhitespaceScope(SavannaWriter writer)
        {
            _writer                    = writer;
            _previous                  = writer.PreservesWhitespace;
            writer.PreservesWhitespace = true;
        }

        /// <summary>Restores the whitespace mode that was active before this scope.</summary>
        public void Dispose()
        {
            _writer?.PreservesWhitespace = _previous;
        }
    }
}
