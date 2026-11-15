#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Globalization;
using System.Text;

namespace Serval.Http;

/// <summary>Writes and flushes events on an existing event-stream response.</summary>
/// <remarks>
///     Await writes in order and keep the response open for the stream's lifetime. This writer does not own or complete
///     the
///     response.
/// </remarks>
public sealed class ServerSentEvents
{
    private readonly Response _response;

    /// <summary>Creates an event writer for the supplied response.</summary>
    /// <param name="response">The response started by Response.StartEventsAsync.</param>
    public ServerSentEvents(Response response)
    {
        _response = response;
    }

    /// <summary>Writes one event and flushes it to the client.</summary>
    /// <param name="data">The event data; each line is emitted as a separate data field.</param>
    /// <param name="eventName">An optional event name without newlines or null characters.</param>
    /// <param name="id">An optional event identifier without newlines or null characters.</param>
    /// <param name="retry">An optional nonnegative reconnect delay, truncated to whole milliseconds.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>An operation that completes after the event has been flushed.</returns>
    /// <exception cref="ArgumentException">
    ///     The event name or identifier contains a newline or null character.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">The retry delay is negative.</exception>
    public async ValueTask SendAsync(string            data,
                                     string?           eventName         = null,
                                     string?           id                = null,
                                     TimeSpan?         retry             = null,
                                     CancellationToken cancellationToken = default)
    {
        ValidateField(eventName);
        ValidateField(id);

        var message = new StringBuilder();
        if (eventName is not null)
        {
            message.Append("event: ").Append(eventName).Append('\n');
        }

        if (id is not null)
        {
            message.Append("id: ").Append(id).Append('\n');
        }

        if (retry is { } interval)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(interval.Ticks);
            message.Append("retry: ").Append(((long)interval.TotalMilliseconds).ToString(CultureInfo.InvariantCulture)).Append('\n');
        }

        foreach (var line in data.ReplaceLineEndings("\n").Split('\n'))
        {
            message.Append("data: ").Append(line).Append('\n');
        }

        message.Append('\n');

        await _response.WriteAsync(Encoding.UTF8.GetBytes(message.ToString()), cancellationToken);
        await _response.FlushAsync(cancellationToken);
    }

    /// <summary>Writes and flushes a comment to keep the event stream active.</summary>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>An operation that completes after the heartbeat has been flushed.</returns>
    public async ValueTask HeartbeatAsync(CancellationToken cancellationToken = default)
    {
        await _response.WriteAsync(": heartbeat\n\n"u8.ToArray(), cancellationToken);
        await _response.FlushAsync(cancellationToken);
    }

    private static void ValidateField(string? value)
    {
        if (value?.Any(c => c is '\r' or '\n' or '\0') == true)
        {
            throw new ArgumentException("SSE fields cannot contain newlines or null characters.");
        }
    }
}
