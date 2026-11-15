#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Buffers;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Serval.Http.Constants;

namespace Serval.Http;

internal static partial class FormDataParser
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    internal static MediaTypeHeaderValue ContentType(string? value)
    {
        if (value is null)
        {
            throw new HttpErrorException(StatusCodes.UnsupportedMediaType, "A form or JSON content type is required.");
        }

        if (!MediaTypeHeaderValue.TryParse(value, out var type) || type.Parameters.GroupBy(parameter => parameter.Name, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
        {
            throw Malformed("Invalid Content-Type.");
        }

        if (type.CharSet is { } charset && !Unquote(charset).Equals("utf-8", StringComparison.OrdinalIgnoreCase))
        {
            throw new HttpErrorException(StatusCodes.UnsupportedMediaType, "Form and POST helpers require UTF-8.");
        }

        return type;
    }

    internal static bool IsForm(MediaTypeHeaderValue type)
        => string.Equals(type.MediaType, MimeTypes.FormUrlEncoded, StringComparison.OrdinalIgnoreCase)
           || string.Equals(type.MediaType, MimeTypes.MultipartFormData, StringComparison.OrdinalIgnoreCase);

    internal static async Task<RequestData> ParseAsync(RequestBodyStorage storage, MediaTypeHeaderValue type, FormOptions options, JsonSerializerOptions jsonOptions, CancellationToken ct)
    {
        try
        {
            await using var input = storage.OpenRead(0, storage.Length);
            if (string.Equals(type.MediaType, MimeTypes.FormUrlEncoded, StringComparison.OrdinalIgnoreCase))
            {
                return await ReadUrlEncodedAsync(input, options, ct);
            }

            if (string.Equals(type.MediaType, MimeTypes.MultipartFormData, StringComparison.OrdinalIgnoreCase))
            {
                return await ReadMultipartAsync(input, storage, type, options, ct);
            }

            var document = await JsonDocument.ParseAsync(input, new JsonDocumentOptions
            {
                MaxDepth            = jsonOptions.MaxDepth,
                AllowTrailingCommas = jsonOptions.AllowTrailingCommas,
                CommentHandling     = jsonOptions.ReadCommentHandling == JsonCommentHandling.Skip ? JsonCommentHandling.Skip : JsonCommentHandling.Disallow,
            }, ct);
            try
            {
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    throw Malformed("POST JSON must be an object.");
                }

                var properties = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
                var count      = 0;
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    CheckName(property.Name, options);
                    count = checked(count + (property.Value.ValueKind == JsonValueKind.Array ? Math.Max(1, property.Value.GetArrayLength()) : 1));
                    if (count > options.MaxFields)
                    {
                        throw Limit();
                    }

                    if (!properties.TryAdd(property.Name, property.Value))
                    {
                        throw Malformed("POST JSON contains duplicate property names.");
                    }

                    CheckJsonValue(property.Value, options);
                }

                return new RequestData(document, properties);
            }
            catch
            {
                document.Dispose();
                throw;
            }
        }
        catch (JsonException exception)
        {
            throw new HttpErrorException(StatusCodes.BadRequest, "The JSON body is invalid.", innerException: exception);
        }
        catch (DecoderFallbackException exception)
        {
            throw new HttpErrorException(StatusCodes.BadRequest, "The form contains invalid UTF-8.", innerException: exception);
        }
        catch (InvalidOperationException exception)
        {
            throw new HttpErrorException(StatusCodes.BadRequest, "The submitted data contains invalid text.", innerException: exception);
        }
    }

    internal static HttpErrorException Limit() => new(StatusCodes.PayloadTooLarge, "The submitted data exceeds a configured form limit.");

    internal static HttpErrorException Malformed(string message) => new(StatusCodes.BadRequest, message);

    private static async Task<RequestData> ReadUrlEncodedAsync(Stream input, FormOptions options, CancellationToken ct)
    {
        var fields = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var token  = new ArrayBufferWriter<byte>();
        var count  = 0;
        var buffer = new byte[8192];
        int read;
        while ((read = await input.ReadAsync(buffer, ct)) != 0)
        {
            for (var index = 0; index < read; index++)
            {
                var value = buffer[index];
                if (value == '&')
                {
                    AddPair();
                    token.Clear();
                    continue;
                }

                if (token.WrittenCount >= ((long)options.MaxKeyLength + options.MaxValueLength) * 12 + 1)
                {
                    throw Limit();
                }

                token.GetSpan(1)[0] = value;
                token.Advance(1);
            }
        }

        AddPair();
        return new RequestData(ToFields(fields), []);

        void AddPair()
        {
            var bytes = token.WrittenSpan;
            if (bytes.IsEmpty)
            {
                return;
            }

            var separator = bytes.IndexOf((byte)'=');
            var name      = Decode(separator < 0 ? bytes : bytes[..separator]);
            var value     = separator < 0 ? "" : Decode(bytes[(separator + 1)..]);
            AddField(fields, name, value, options, ref count);
        }
    }

    private static async Task<RequestData> ReadMultipartAsync(Stream input, RequestBodyStorage storage, MediaTypeHeaderValue type, FormOptions options, CancellationToken ct)
    {
        var fields = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var files  = new List<UploadedFile>();
        var count  = 0;
        await StreamMultipartAsync(input, type, options, async (section, token) =>
        {
            var body = (MultipartBodyStream)section.Body;
            if (section.FileName is not null)
            {
                await body.CopyToAsync(Stream.Null, token);
                files.Add(new UploadedFile(section.Name, section.FileName, section.ContentType, storage, body.StartOffset, body.BytesRead));
            }
            else
            {
                using var content = new MemoryStream();
                await body.CopyToAsync(content, token);
                AddField(fields, section.Name, Utf8.GetString(content.GetBuffer().AsSpan(0, checked((int)content.Length))), options, ref count);
            }
        }, ct);

        return new RequestData(ToFields(fields), files);
    }

    private static void AddField(Dictionary<string, List<string>> fields, string name, string value, FormOptions options, ref int count)
    {
        CheckName(name, options);
        if (++count > options.MaxFields || value.Length > options.MaxValueLength)
        {
            throw Limit();
        }

        if (!fields.TryGetValue(name, out var values))
        {
            fields.Add(name, values = []);
        }

        values.Add(value);
    }

    private static void CheckName(string name, FormOptions options)
    {
        if (name.Length > options.MaxKeyLength)
        {
            throw Limit();
        }
    }

    private static void CheckJsonValue(JsonElement value, FormOptions options)
    {
        if (value.ValueKind == JsonValueKind.String && value.GetString()!.Length > options.MaxValueLength)
        {
            throw Limit();
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                CheckJsonValue(item, options);
            }
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                CheckName(property.Name, options);
                if (!names.Add(property.Name))
                {
                    throw Malformed("POST JSON contains duplicate property names.");
                }

                CheckJsonValue(property.Value, options);
            }
        }
    }

    private static Dictionary<string, string[]> ToFields(Dictionary<string, List<string>> fields)
        => fields.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.OrdinalIgnoreCase);

    private static string Decode(ReadOnlySpan<byte> encoded)
    {
        var decoded = new byte[encoded.Length];
        var count   = 0;
        for (var index = 0; index < encoded.Length; index++)
        {
            var value = encoded[index];
            if (value == '%')
            {
                if (index + 2 >= encoded.Length)
                {
                    throw Malformed("Invalid percent escape in form data.");
                }

                var high = Hex(encoded[index + 1]);
                var low  = Hex(encoded[index + 2]);
                if (high < 0 || low < 0)
                {
                    throw Malformed("Invalid percent escape in form data.");
                }

                value =  (byte)(high << 4 | low);
                index += 2;
            }
            else if (value == '+')
            {
                value = (byte)' ';
            }

            decoded[count++] = value;
        }

        return Utf8.GetString(decoded.AsSpan(0, count));
    }

    private static int Hex(byte value) => value switch
    {
        >= (byte)'0' and <= (byte)'9' => value       - '0',
        >= (byte)'a' and <= (byte)'f' => value - 'a' + 10,
        >= (byte)'A' and <= (byte)'F' => value - 'A' + 10,
        _                             => -1,
    };

    private static string Unquote(string value)
    {
        if (value.Length < 2 || value[0] != '"' || value[^1] != '"')
        {
            return value;
        }

        var result = new StringBuilder(value.Length - 2);
        for (var index = 1; index < value.Length - 1; index++)
        {
            if (value[index] == '\\' && index + 1 < value.Length - 1)
            {
                index++;
            }

            result.Append(value[index]);
        }

        return result.ToString();
    }

    private sealed class MultipartCursor
    {
        internal long   Position => _offset + _start;
        private  byte[] _buffer = new byte[32768];
        private  int    _end;
        private  bool   _eof;
        private  long   _offset;

        private          int    _start;
        private readonly Stream _stream;

        internal MultipartCursor(Stream stream)
        {
            _stream = stream;
        }

        internal async ValueTask<string> ReadLineAsync(int limit, CancellationToken ct, bool allowEnd = false)
        {
            var line = new ArrayBufferWriter<byte>();
            while (true)
            {
                await EnsureAsync(2, ct);
                var available = _buffer.AsSpan(_start, _end - _start);
                var ending    = available.IndexOf("\r\n"u8);
                var take      = ending >= 0 ? ending : _eof && allowEnd ? available.Length : Math.Max(0, available.Length - 1);
                if (line.WrittenCount + take + 2 > limit)
                {
                    throw Limit();
                }

                line.Write(available[..take]);
                _start += take;
                if (ending >= 0)
                {
                    _start += 2;
                    return Utf8.GetString(line.WrittenSpan);
                }

                if (_eof)
                {
                    if (allowEnd && line.WrittenCount != 0)
                    {
                        return Utf8.GetString(line.WrittenSpan);
                    }

                    throw Malformed("Incomplete multipart header or boundary.");
                }
            }
        }

        internal async ValueTask<(int Count, bool? Final)> ReadPartAsync(Memory<byte> destination, byte[] delimiter, int paddingLimit, CancellationToken ct)
        {
            if (destination.IsEmpty)
            {
                return (0, null);
            }

            while (true)
            {
                await EnsureAsync(delimiter.Length + 4, ct);
                var match = _buffer.AsSpan(_start, _end - _start).IndexOf(delimiter);
                if (match == 0)
                {
                    var (consumed, final) = await ReadBoundaryEndingAsync(delimiter.Length, paddingLimit, ct);
                    if (consumed != 0)
                    {
                        _start += consumed;
                        return (0, final);
                    }

                    // A boundary prefix inside file bytes is ordinary data unless the line ending also matches.
                    match = 1;
                }

                if (match < 0 && _eof)
                {
                    throw Malformed("The multipart body is missing its closing boundary.");
                }

                var available = match > 0 ? match : Math.Max(1, _end - _start - delimiter.Length + 1);
                var count     = Math.Min(destination.Length, available);
                _buffer.AsMemory(_start, count).CopyTo(destination);
                _start += count;
                return (count, null);
            }
        }

        internal async ValueTask DrainAsync(CancellationToken ct)
        {
            _start = _end;
            while (await _stream.ReadAsync(_buffer, ct) != 0) { }
        }

        private async ValueTask<(int Consumed, bool Final)> ReadBoundaryEndingAsync(int length, int paddingLimit, CancellationToken ct)
        {
            await EnsureAsync(length + 2, ct);
            var final   = _buffer.AsSpan(_start + length, _end - _start - length).StartsWith("--"u8);
            var ending  = length + (final ? 2 : 0);
            var padding = 0;
            while (true)
            {
                await EnsureAsync(ending + 2, ct);
                var suffix = _buffer.AsSpan(_start + ending, _end - _start - ending);
                if (suffix.StartsWith("\r\n"u8))
                {
                    return (ending + 2, final);
                }

                if (suffix.IsEmpty && _eof && final)
                {
                    return (ending, true);
                }

                if (suffix.IsEmpty || suffix[0] is not ((byte)' ' or (byte)'\t'))
                {
                    return (0, false);
                }

                if (++padding > paddingLimit)
                {
                    throw Limit();
                }

                ending++;
            }
        }

        private async ValueTask EnsureAsync(int count, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (_end - _start >= count || _eof)
            {
                return;
            }

            _buffer.AsSpan(_start, _end - _start).CopyTo(_buffer);
            _offset += _start;
            _end    -= _start;
            _start  =  0;
            if (count > _buffer.Length)
            {
                Array.Resize(ref _buffer, Math.Max(count, checked(_buffer.Length * 2)));
            }

            while (_end < count && !_eof)
            {
                var read = await _stream.ReadAsync(_buffer.AsMemory(_end), ct);
                _end += read;
                _eof =  read == 0;
            }
        }
    }
}
