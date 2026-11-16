#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Serval.Http.Constants;

namespace Serval.Http;

/// <summary>Represents an incoming HTTP request and provides bounded body readers.</summary>
/// <remarks>
///     Raw body and whole-body JSON reads consume the stream. Form and POST readers cache their parsed data.
///     Choose one reading mode per request. The framework disposes buffered uploads after request processing.
/// </remarks>
public sealed partial class Request : IAsyncDisposable
{
    /// <summary>The HTTP method supplied by the transport.</summary>
    public string Method { get; }

    /// <summary>The absolute request URL.</summary>
    public Uri Url { get; }

    /// <summary>The HTTP version used by the transport.</summary>
    public Version ProtocolVersion { get; }

    /// <summary>The request headers, frozen when the request is constructed.</summary>
    public Headers Headers { get; }

    /// <summary>
    ///     Lazily parsed, case-insensitive query parameters. Repeated values keep their order and plus signs decode to
    ///     spaces.
    /// </summary>
    public IReadOnlyDictionary<string, string[]> Query => _query ??= ParseQuery(Url.Query);

    /// <summary>
    ///     Lazily parsed, case-sensitive cookie names and URL-decoded values. The first occurrence of each name wins.
    /// </summary>
    public IReadOnlyDictionary<string, string> Cookies => _cookies ??= ParseCookies();

    internal JsonSerializerOptions JsonOptions    { get; set; } = JsonSerializerOptions.Web;
    internal CancellationToken     RequestAborted { get; set; }

    private long                          _bodyBytesRead;
    private bool                          _bodyTooLarge;
    private Dictionary<string, string>?   _cookies;
    private Dictionary<string, string[]>? _query;
    private long                          _maxBodyBytes;

    private readonly Stream _body;

    /// <summary>Creates a request over a transport-owned body stream.</summary>
    /// <param name="method">The HTTP method.</param>
    /// <param name="url">The absolute request URL.</param>
    /// <param name="protocolVersion">The incoming HTTP protocol version.</param>
    /// <param name="headers">The headers to freeze and expose.</param>
    /// <param name="body">The body stream; the transport retains ownership.</param>
    /// <param name="maxBodyBytes">The cumulative limit on bytes read from the body.</param>
    /// <param name="connection">Optional transport endpoint information.</param>
    /// <remarks>
    ///     The body stream must remain available throughout request processing. This object does not dispose it.
    /// </remarks>
    public Request(string method, Uri url, Version protocolVersion, Headers headers, Stream body, long maxBodyBytes, RequestConnection? connection = null)
    {
        Method          = method;
        Url             = url;
        ProtocolVersion = protocolVersion;
        Headers         = headers;
        _body           = body;
        _maxBodyBytes   = maxBodyBytes;
        Connection      = connection ?? RequestConnection.Unknown;
        ClientAddress   = Connection.RemoteAddress;
        Scheme          = url.Scheme;
        Host            = url.Authority;

        headers.Freeze();
    }

    /// <summary>Reads the remaining body into a byte array.</summary>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The remaining body bytes.</returns>
    /// <remarks>
    ///     Reads the remaining stream without rewinding. The supplied token is combined with the framework's request
    ///     cancellation token.
    /// </remarks>
    /// <exception cref="HttpErrorException">The cumulative body limit is exceeded; the status is 413.</exception>
    /// <exception cref="OperationCanceledException">The caller or request cancellation token is canceled.</exception>
    public async ValueTask<byte[]> ReadBodyAsync(CancellationToken cancellationToken = default)
    {
        using var body = await ReadBodyBufferAsync(cancellationToken);
        return body.ToArray();
    }

    /// <summary>Decodes the remaining body as UTF-8.</summary>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The remaining body text; invalid UTF-8 uses replacement characters.</returns>
    /// <remarks>
    ///     Reads the remaining stream without rewinding. The supplied token is combined with the framework's request
    ///     cancellation token.
    /// </remarks>
    /// <exception cref="HttpErrorException">The cumulative body limit is exceeded; the status is 413.</exception>
    /// <exception cref="OperationCanceledException">The caller or request cancellation token is canceled.</exception>
    public async ValueTask<string> ReadTextAsync(CancellationToken cancellationToken = default)
    {
        using var body = await ReadBodyBufferAsync(cancellationToken);
        return Encoding.UTF8.GetString(body.GetBuffer().AsSpan(0, (int)body.Length));
    }

    /// <summary>Deserializes a non-null JSON value from the remaining request body.</summary>
    /// <typeparam name="T">The expected JSON value type.</typeparam>
    /// <param name="typeInfo">Metadata and serializer settings used to deserialize the body.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The deserialized value.</returns>
    /// <remarks>
    ///     Reads the remaining stream without rewinding. The supplied token is combined with the framework's request
    ///     cancellation token.
    ///     Only the root value is guaranteed to be non-null. Model incoming reference members and collection elements
    ///     as nullable and validate them before use; required properties enforce presence, not non-null values.
    /// </remarks>
    /// <exception cref="HttpErrorException">
    ///     The media type is not JSON (415), the body is invalid or null JSON (400), or the body limit is exceeded (413).
    /// </exception>
    /// <exception cref="OperationCanceledException">The caller or request cancellation token is canceled.</exception>
    public async ValueTask<T> ReadJsonAsync<T>(JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken = default)
    {
        var contentType = Headers[HeaderNames.ContentType]?.Split(';')[0].Trim();
        if (contentType is null || !contentType.Equals(MimeTypes.Json, StringComparison.OrdinalIgnoreCase) && !contentType.EndsWith("+json", StringComparison.OrdinalIgnoreCase))
        {
            throw new HttpErrorException(StatusCodes.UnsupportedMediaType, "A JSON content type is required.");
        }

        using var body = await ReadBodyBufferAsync(cancellationToken);
        try
        {
            return JsonSerializer.Deserialize(body.GetBuffer().AsSpan(0, (int)body.Length), typeInfo) ?? throw new HttpErrorException(StatusCodes.BadRequest, "A JSON value is required.");
        }
        catch (JsonException exception)
        {
            throw new HttpErrorException(StatusCodes.BadRequest, "The JSON body is invalid.", innerException: exception);
        }
    }

    /// <summary>Deserializes a non-null JSON value from the remaining request body.</summary>
    /// <typeparam name="T">The expected JSON value type.</typeparam>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The deserialized value.</returns>
    /// <remarks>
    ///     Reads the remaining stream without rewinding. The supplied token is combined with the framework's request
    ///     cancellation token. Uses the framework's configured JSON options.
    ///     Only the root value is guaranteed to be non-null. Model incoming reference members and collection elements
    ///     as nullable and validate them before use; required properties enforce presence, not non-null values.
    /// </remarks>
    /// <exception cref="HttpErrorException">
    ///     The media type is not JSON (415), the body is invalid or null JSON (400), or the body limit is exceeded (413).
    /// </exception>
    /// <exception cref="OperationCanceledException">The caller or request cancellation token is canceled.</exception>
    public ValueTask<T> ReadJsonAsync<T>(CancellationToken cancellationToken = default) => ReadJsonAsync(JsonOptions.GetTypeInfo<T>(), cancellationToken);

    /// <summary>Parses exactly one query value using invariant culture.</summary>
    /// <typeparam name="T">The target type that can parse a string.</typeparam>
    /// <param name="name">The case-insensitive query parameter name.</param>
    /// <returns>The parsed value.</returns>
    /// <exception cref="HttpErrorException">
    ///     The parameter is absent, repeated, or invalid; the status is 400.
    /// </exception>
    public T QueryValue<T>(string name) where T : IParsable<T>
    {
        if (!Query.TryGetValue(name, out var values) || values.Length != 1 || !T.TryParse(values[0], CultureInfo.InvariantCulture, out var value))
        {
            throw new HttpErrorException(StatusCodes.BadRequest, $"Query parameter '{name}' is missing or invalid.");
        }

        return value;
    }

    private async ValueTask<MemoryStream> ReadBodyBufferAsync(CancellationToken cancellationToken)
    {
        var result = new MemoryStream();
        try
        {
            await CopyBodyToAsync(result, cancellationToken);
        }
        catch
        {
            await result.DisposeAsync();
            throw;
        }

        return result;
    }

    private Dictionary<string, string> ParseCookies()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var header in Headers.GetValues(HeaderNames.Cookie))
        {
            foreach (var part in header.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                var separator = part.IndexOf('=');
                if (separator > 0)
                {
                    result.TryAdd(part[..separator], Uri.UnescapeDataString(part[(separator + 1)..]));
                }
            }
        }

        return result;
    }

    private static Dictionary<string, string[]> ParseQuery(string query)
    {
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

            // Repeated parameter names can use the existing dictionary key without another string allocation.
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

    private static string Decode(ReadOnlySpan<char> value)
    {
        if (value.Contains('+'))
        {
            value = value.ToString().Replace('+', ' ');
        }

        return Uri.UnescapeDataString(value);
    }
}
