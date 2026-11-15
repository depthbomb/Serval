#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Buffers;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Serval.Http.Constants;

namespace Serval.Http;

/// <summary>Builds an HTTP response over a transport-specific writer.</summary>
/// <remarks>
///     Set status and headers before starting or writing the response. Writes are intended to be awaited in order; callers
///     must coordinate concurrent producers.
/// </remarks>
public abstract partial class Response
{
    /// <summary>Response headers, writable until the response starts, upgrades, or ends.</summary>
    public Headers Headers { get; } = new();

    /// <summary>Helpers that append Set-Cookie headers before the response starts.</summary>
    public ResponseCookies Cookies { get; }

    /// <summary>Whether the final response has started, an upgrade has begun, or a header write has failed.</summary>
    public bool HasStarted { get; private set; }

    /// <summary>Whether this transport can send HTTP 103 responses before the final response.</summary>
    /// <remarks>Defaults to false. Serval's HTTP/2 and HTTP/3 transports support Early Hints.</remarks>
    public virtual bool SupportsEarlyHints => false;

    /// <summary>The final response status, defaulting to 200.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 200 through 599.</exception>
    /// <exception cref="InvalidOperationException">The response has already started or ended.</exception>
    public int StatusCode
    {
        get;
        set
        {
            if (HasStarted || _ended)
            {
                throw new InvalidOperationException("The response status is no longer writable.");
            }

            ArgumentOutOfRangeException.ThrowIfLessThan(value, 200);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 599);
            field = value;
        }
    } = StatusCodes.Ok;

    internal JsonSerializerOptions JsonOptions { get; set; } = JsonSerializerOptions.Web;

    private bool _earlyHintsFailed;

    /// <summary>Creates the mutable headers and cookie writer for a transport response.</summary>
    protected Response()
    {
        Cookies = new ResponseCookies(Headers);
    }

    /// <summary>Sends an HTTP 103 Early Hints response without starting the final response.</summary>
    /// <param name="headers">The hint fields, usually Link headers describing resources to preload.</param>
    /// <param name="ct">Cancels the operation.</param>
    /// <returns>True when the hints were sent; false when this transport does not support Early Hints.</returns>
    /// <remarks>
    ///     The supplied headers are copied before sending and are neither frozen nor added to the final response.
    ///     Await each send before writing again. Multiple hint responses may precede the final response, whose status,
    ///     headers, and cookies remain writable. Unsupported transports leave the response untouched.
    ///     If a transport write fails, the response becomes unwritable and must be aborted because a partial header
    ///     block may have reached the client. Sending hints does not guarantee that a client will act on them.
    /// </remarks>
    /// <exception cref="ArgumentNullException">The headers are null.</exception>
    /// <exception cref="ArgumentException">The hints contain content-length or connection-specific fields.</exception>
    /// <exception cref="InvalidOperationException">The final response has already started or a header write failed.</exception>
    /// <exception cref="OperationCanceledException">The operation was canceled.</exception>
    public async ValueTask<bool> SendEarlyHintsAsync(Headers headers, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(headers);
        EnsureCanWrite();
        if (_starting)
        {
            throw new InvalidOperationException("A response startup callback cannot send Early Hints.");
        }

        if (HasStarted)
        {
            throw new InvalidOperationException("The response has already started.");
        }

        ct.ThrowIfCancellationRequested();

        var hints = new Headers();
        foreach (var (name, values) in headers)
        {
            var forbidden = name.Equals(HeaderNames.ContentLength, StringComparison.OrdinalIgnoreCase)    ||
                            name.Equals(HeaderNames.TransferEncoding, StringComparison.OrdinalIgnoreCase) ||
                            name.Equals(HeaderNames.Connection, StringComparison.OrdinalIgnoreCase)       ||
                            name.Equals(HeaderNames.Upgrade, StringComparison.OrdinalIgnoreCase)          ||
                            name.Equals(HeaderNames.KeepAlive, StringComparison.OrdinalIgnoreCase)        ||
                            name.Equals(HeaderNames.ProxyConnection, StringComparison.OrdinalIgnoreCase)  ||
                            name.Equals(HeaderNames.TE, StringComparison.OrdinalIgnoreCase)               ||
                            name.Equals(HeaderNames.Trailer, StringComparison.OrdinalIgnoreCase);
            if (forbidden)
            {
                throw new ArgumentException($"The '{name}' header is not allowed in Early Hints.", nameof(headers));
            }

            foreach (var value in values)
            {
                hints.Append(name, value);
            }
        }

        if (!SupportsEarlyHints)
        {
            return false;
        }

        hints.Freeze();
        try
        {
            await OnSendEarlyHintsAsync(hints, ct);
        }
        catch
        {
            // A partial informational frame cannot safely be followed by an error response.
            _earlyHintsFailed = true;
            HasStarted        = true;
            Headers.Freeze();
            throw;
        }

        return true;
    }

    /// <summary>Freezes headers and starts the transport response once.</summary>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>An operation that completes when response startup finishes.</returns>
    /// <remarks>
    ///     Repeated calls do nothing once startup finishes while the response is active. A failed transport start still
    ///     leaves the response marked as started. Completed or aborted responses reject further starts and writes.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    ///     Content-Length is set for status 204 or alongside Transfer-Encoding, or an earlier Early Hints write failed.
    /// </exception>
    public async ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        EnsureCanWrite();
        if (_earlyHintsFailed)
        {
            throw new InvalidOperationException("An Early Hints write failed. Abort this response before sending anything else.");
        }

        if (!HasStarted)
        {
            if (_startingCallbacks is { Count: > 0 } || LifecycleStarting is not null)
            {
                await RunStartingAsync(cancellationToken);
            }

            // Reject contradictory framing before committing headers on any transport.
            if (
                Headers[HeaderNames.ContentLength] is not null &&
                (StatusCode == StatusCodes.NoContent || Headers[HeaderNames.TransferEncoding] is not null)
            )
            {
                throw new InvalidOperationException("Content-Length is forbidden for this response framing.");
            }

            HasStarted = true;
            Headers.Freeze();

            await OnStartAsync(cancellationToken);
        }
    }

    /// <summary>Starts the response if needed and writes body bytes.</summary>
    /// <param name="bytes">The bytes to write; keep the memory valid until the operation completes.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>An operation that completes when the transport has accepted the bytes.</returns>
    /// <remarks>
    ///     Body bytes are suppressed for status 204 and 304. Transports also suppress bodies for HEAD requests.
    /// </remarks>
    public async ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
    {
        await StartAsync(cancellationToken);

        if (StatusCode is not (StatusCodes.NoContent or StatusCodes.NotModified))
        {
            await OnWriteAsync(bytes, cancellationToken);
        }
    }

    /// <summary>Writes UTF-8 text, setting a plain-text content type if no type is set before startup.</summary>
    /// <param name="text">The text to encode and write.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>An operation that completes when the bytes have been written.</returns>
    public ValueTask WriteTextAsync(string text, CancellationToken cancellationToken = default)
    {
        if (!HasStarted)
        {
            Headers[HeaderNames.ContentType] ??= MimeTypes.Text;
        }

        return WriteAsync(Encoding.UTF8.GetBytes(text), cancellationToken);
    }

    /// <summary>Serializes a value to UTF-8 JSON and writes it to the response.</summary>
    /// <typeparam name="T">The serialized value type.</typeparam>
    /// <param name="value">The value to serialize.</param>
    /// <param name="typeInfo">Metadata used to serialize the value.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>An operation that completes when the JSON bytes have been written.</returns>
    /// <remarks>
    ///     Uses the supplied serialization metadata. The content type defaults to application/json while headers are
    ///     writable; an existing type is preserved.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The response has started without a Content-Type header.</exception>
    public ValueTask WriteJsonAsync<T>(T value, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken = default)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, typeInfo);

        Headers[HeaderNames.ContentType] ??= MimeTypes.Json;

        return WriteAsync(bytes, cancellationToken);
    }

    /// <summary>Serializes a value to UTF-8 JSON and writes it to the response.</summary>
    /// <typeparam name="T">The serialized value type.</typeparam>
    /// <param name="value">The value to serialize.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>An operation that completes when the JSON bytes have been written.</returns>
    /// <remarks>
    ///     Uses the framework's configured JSON options. The content type defaults to application/json while headers are
    ///     writable; an existing type is preserved.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The response has started without a Content-Type header.</exception>
    public ValueTask WriteJsonAsync<T>(T value, CancellationToken cancellationToken = default)
        => WriteJsonAsync(value, JsonOptions.GetTypeInfo<T>(), cancellationToken);

    /// <summary>Streams a file with its content length and optional attachment filename.</summary>
    /// <param name="path">The file path to open for read-only, sequential access.</param>
    /// <param name="contentType">The media type, or null for application/octet-stream.</param>
    /// <param name="downloadName">An optional nonempty attachment filename without control characters.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>An operation that completes after the opened file has been sent and disposed.</returns>
    /// <remarks>
    ///     Call before response startup. The byte count is taken when the file opens; later growth is ignored.
    /// </remarks>
    /// <exception cref="HttpErrorException">The file or its directory does not exist; the status is 404.</exception>
    /// <exception cref="ArgumentException">The download filename is empty or contains control characters.</exception>
    /// <exception cref="IOException">The file ends before its original length, or file access fails.</exception>
    /// <exception cref="InvalidOperationException">The response headers are already frozen.</exception>
    public async ValueTask WriteFileAsync(string            path,
                                          string?           contentType       = null,
                                          string?           downloadName      = null,
                                          CancellationToken cancellationToken = default)
    {
        FileStream file;
        try
        {
            file = new FileStream(path, new FileStreamOptions
            {
                Mode    = FileMode.Open,
                Access  = FileAccess.Read,
                Share   = FileShare.Read,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            });
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new HttpErrorException(StatusCodes.NotFound, "The requested file was not found.", innerException: exception);
        }

        await using (file)
        {
            // Commit to one length. Later file growth must not leak extra bytes past Content-Length.
            var length = file.Length;
            Headers[HeaderNames.ContentType]   = contentType ?? MimeTypes.OctetStream;
            Headers[HeaderNames.ContentLength] = length.ToString(CultureInfo.InvariantCulture);
            if (downloadName is not null)
            {
                if (downloadName.Length == 0 || downloadName.Any(character => character is < ' ' or '\x7f'))
                {
                    throw new ArgumentException("Invalid download filename.", nameof(downloadName));
                }

                Headers[HeaderNames.ContentDisposition] = new ContentDispositionHeaderValue("attachment")
                {
                    FileNameStar = downloadName,
                }.ToString();
            }

            await StartAsync(cancellationToken);

            var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
            try
            {
                var remaining = length;
                while (remaining > 0)
                {
                    var read = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(remaining, buffer.Length)), cancellationToken);
                    if (read == 0)
                    {
                        throw new IOException("The response file changed while it was being sent.");
                    }

                    await WriteAsync(buffer.AsMemory(0, read), cancellationToken);

                    remaining -= read;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    /// <summary>Sets the Location header and starts a redirect response.</summary>
    /// <param name="location">The redirect target, written as supplied after header validation.</param>
    /// <param name="statusCode">One of 301, 302, 303, 307, or 308; defaults to 303.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>An operation that completes when the redirect headers are sent.</returns>
    /// <remarks>Choose and validate the destination in application code.</remarks>
    /// <exception cref="ArgumentOutOfRangeException">The status is not one of the supported redirect codes.</exception>
    /// <exception cref="InvalidOperationException">The response has already started.</exception>
    public ValueTask RedirectAsync(string            location,
                                   int               statusCode        = StatusCodes.SeeOther,
                                   CancellationToken cancellationToken = default)
    {
        if (
            statusCode is not (StatusCodes.MovedPermanently or
                               StatusCodes.Found or
                               StatusCodes.SeeOther or
                               StatusCodes.TemporaryRedirect or StatusCodes.PermanentRedirect)
        )
        {
            throw new ArgumentOutOfRangeException(nameof(statusCode));
        }

        StatusCode                    = statusCode;
        Headers[HeaderNames.Location] = location;

        return StartAsync(cancellationToken);
    }

    /// <summary>Starts and flushes an event-stream response with caching disabled.</summary>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A writer for subsequent server-sent events.</returns>
    /// <remarks>
    ///     Call before response startup. For a long-lived stream, the application can disable the framework deadline
    ///     through RequestContext.DisableRequestTimeout.
    /// </remarks>
    public async ValueTask<ServerSentEvents> StartEventsAsync(CancellationToken cancellationToken = default)
    {
        Headers[HeaderNames.ContentType]  = MimeTypes.EventStream;
        Headers[HeaderNames.CacheControl] = CacheControlDirectives.NoCache;

        await StartAsync(cancellationToken);
        await FlushAsync(cancellationToken);

        return new ServerSentEvents(this);
    }

    /// <summary>Accepts a WebSocket upgrade when the transport supports it.</summary>
    /// <param name="subProtocol">The negotiated subprotocol, or null when none is selected.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The accepted WebSocket, which the caller must dispose.</returns>
    /// <exception cref="HttpErrorException">
    ///     The transport does not support WebSockets; the default implementation reports 501.
    /// </exception>
    public virtual ValueTask<WebSocket> AcceptWebSocketAsync(string?           subProtocol       = null,
                                                             CancellationToken cancellationToken = default)
        => ValueTask.FromException<WebSocket>(new HttpErrorException(StatusCodes.NotImplemented, "WebSockets are unavailable on this transport."));

    /// <summary>Flushes buffered response bytes to the transport.</summary>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>An operation that completes when the transport flushes.</returns>
    public abstract ValueTask FlushAsync(CancellationToken cancellationToken = default);

    /// <summary>Finishes the response and releases its transport resources.</summary>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>An operation that completes when the response is finished.</returns>
    /// <remarks>
    ///     Implementations should start an otherwise empty response before completing it, then call MarkCompleted.
    ///     Completion callbacks run when the request handler finishes processing the request.
    /// </remarks>
    public abstract ValueTask CompleteAsync(CancellationToken cancellationToken = default);

    /// <summary>Stops the response without attempting to send a replacement body.</summary>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>An operation that completes when the transport aborts.</returns>
    /// <remarks>Used after a disconnect or a failure after headers have started. Implementations must call MarkAborted.</remarks>
    public abstract ValueTask AbortAsync(CancellationToken cancellationToken = default);

    /// <summary>Commits the status and frozen headers to the transport.</summary>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>An operation that completes when the transport starts the response.</returns>
    /// <remarks>Called once by StartAsync after HasStarted is set and headers are frozen.</remarks>
    protected abstract ValueTask OnStartAsync(CancellationToken cancellationToken);

    /// <summary>Writes and flushes a complete HTTP 103 header block without ending the response stream.</summary>
    /// <param name="headers">A validated, frozen snapshot of the hint fields.</param>
    /// <param name="ct">Cancels the write.</param>
    /// <returns>An operation that completes when the header block has been sent.</returns>
    /// <remarks>Override together with SupportsEarlyHints. The default implementation throws if invoked.</remarks>
    /// <exception cref="NotSupportedException">The transport has not implemented Early Hints.</exception>
    protected virtual ValueTask OnSendEarlyHintsAsync(Headers headers, CancellationToken ct)
        => ValueTask.FromException(new NotSupportedException("Early Hints are unavailable on this transport."));

    /// <summary>Writes body bytes after response startup.</summary>
    /// <param name="bytes">The bytes to send without retaining their memory beyond the returned operation.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>An operation that completes when the transport has accepted the bytes.</returns>
    /// <remarks>
    ///     The base class filters statuses without a body. Transport implementations must also handle HEAD suppression and
    ///     their own framing.
    /// </remarks>
    protected abstract ValueTask OnWriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken);

    /// <summary>Marks a protocol upgrade as started and freezes the response headers.</summary>
    protected async ValueTask MarkUpgradedAsync(CancellationToken ct = default)
    {
        EnsureCanWrite();
        await RunStartingAsync(ct);
        HasStarted = true;
        Headers.Freeze();
    }
}
