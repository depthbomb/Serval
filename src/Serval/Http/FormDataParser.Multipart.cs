#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Net.Http.Headers;
using System.Runtime.ExceptionServices;
using System.Text;
using Serval.Http.Constants;

namespace Serval.Http;

internal static partial class FormDataParser
{
    internal static async ValueTask StreamMultipartAsync(Stream                                               input,
                                                         MediaTypeHeaderValue                                 type,
                                                         FormOptions                                          options,
                                                         Func<MultipartSection, CancellationToken, ValueTask> readSection,
                                                         CancellationToken                                    ct)
    {
        try
        {
            await ReadSectionsAsync(input, type, options, readSection, ct);
        }
        catch (DecoderFallbackException)
        {
            throw Malformed("Invalid UTF-8 in multipart data.");
        }
    }

    private static async ValueTask ReadSectionsAsync(Stream                                               input,
                                                     MediaTypeHeaderValue                                 type,
                                                     FormOptions                                          options,
                                                     Func<MultipartSection, CancellationToken, ValueTask> readSection,
                                                     CancellationToken                                    ct)
    {
        var boundary = Unquote(type.Parameters.FirstOrDefault(parameter => parameter.Name.Equals("boundary", StringComparison.OrdinalIgnoreCase))?.Value ?? "");
        if (boundary.Length is < 1 or > 70 || boundary.EndsWith(' ') || boundary.Any(character => !char.IsAsciiLetterOrDigit(character) && !"'()+_,-./:=? ".Contains(character)))
        {
            throw Malformed("Invalid multipart boundary.");
        }

        var    cursor   = new MultipartCursor(input);
        var    opening  = "--" + boundary;
        var    preamble = 0;
        string line;
        do
        {
            line     =  await cursor.ReadLineAsync(options.MaxMultipartHeaderBytes - preamble, ct, true);
            preamble += Utf8.GetByteCount(line) + 2;
            line     =  line.TrimEnd(' ', '\t');
            if (line == opening + "--")
            {
                await cursor.DrainAsync(ct);
                return;
            }
        } while (line != opening);

        var delimiter = Encoding.ASCII.GetBytes("\r\n" + opening);
        var fields    = 0;
        var files     = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var headers = await ReadPartHeadersAsync(cursor, options, ct);
            if (!ContentDispositionHeaderValue.TryParse(headers[HeaderNames.ContentDisposition], out var disposition)
                || !disposition.DispositionType.Equals("form-data", StringComparison.OrdinalIgnoreCase)
                || disposition.Parameters.GroupBy(parameter => parameter.Name, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1)
                || disposition.Name is null || headers["Content-Transfer-Encoding"] is not null)
            {
                throw Malformed("A multipart part needs a form-data disposition and a name, without transfer encoding.");
            }

            var name = Unquote(disposition.Name);
            CheckName(name, options);
            var     isFile   = disposition.Parameters.Any(parameter => parameter.Name.Equals("filename", StringComparison.OrdinalIgnoreCase) || parameter.Name.Equals("filename*", StringComparison.OrdinalIgnoreCase));
            string? fileName = null;
            if (isFile)
            {
                if (++files > options.MaxFiles)
                {
                    throw Limit();
                }

                fileName = disposition.FileNameStar ?? disposition.FileName;
                if (fileName is null)
                {
                    throw Malformed("Invalid multipart filename.");
                }

                fileName = disposition.FileNameStar ?? Unquote(fileName);
            }
            else
            {
                if (++fields > options.MaxFields)
                {
                    throw Limit();
                }

                if (headers[HeaderNames.ContentType] is { } fieldType)
                {
                    ContentType(fieldType);
                }
            }

            await using var body    = new MultipartBodyStream(cursor, delimiter, options, isFile, ct);
            var             section = new MultipartSection(name, fileName, headers[HeaderNames.ContentType] ?? (isFile ? MimeTypes.OctetStream : MimeTypes.Text), headers, body);
            await readSection(section, ct);
            // Even skipped sections must satisfy framing and limits before advancing to the next one.
            await body.CopyToAsync(Stream.Null, ct);
            if (body.Final)
            {
                await cursor.DrainAsync(ct);
                return;
            }
        }
    }

    private static async ValueTask<Headers> ReadPartHeadersAsync(MultipartCursor cursor, FormOptions options, CancellationToken ct)
    {
        var    headers = new Headers();
        var    bytes   = 0;
        var    count   = 0;
        string line;
        while ((line = await cursor.ReadLineAsync(options.MaxMultipartHeaderBytes - bytes, ct)).Length != 0)
        {
            bytes += Utf8.GetByteCount(line) + 2;
            if (++count > options.MaxMultipartHeaders)
            {
                throw Limit();
            }

            var colon = line.IndexOf(':');
            if (colon <= 0                                                                                                                                                                             || line[..colon].Any(character => !char.IsAsciiLetterOrDigit(character) && !"!#$%&'*+-.^_`|~".Contains(character))
                                                                                                                                                                                                       || line.Any(character => character == '\x7f' || character < ' ' && character != '\t') || headers[line[..colon]] is not null)
            {
                throw Malformed("Invalid or repeated multipart header.");
            }

            headers[line[..colon]] = line[(colon + 1)..].Trim();
        }

        headers.Freeze();
        return headers;
    }

    private sealed class MultipartBodyStream : Stream
    {
        public override bool CanRead  => !_disposed;
        public override bool CanSeek  => false;
        public override bool CanWrite => false;
        public override long Length   => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        internal long StartOffset { get; }
        internal long BytesRead   { get; private set; }
        internal bool Final       { get; private set; }

        private bool                   _ended;
        private bool                   _disposed;
        private int                    _reading;
        private long                   _characters;
        private ExceptionDispatchInfo? _failure;

        private readonly MultipartCursor   _cursor;
        private readonly byte[]            _delimiter;
        private readonly FormOptions       _options;
        private readonly long              _maxBytes;
        private readonly Decoder?          _decoder;
        private readonly CancellationToken _ct;

        internal MultipartBodyStream(MultipartCursor cursor, byte[] delimiter, FormOptions options, bool isFile, CancellationToken ct)
        {
            _cursor     = cursor;
            _delimiter  = delimiter;
            _options    = options;
            _maxBytes   = isFile ? options.MaxFileBytes : (long)options.MaxValueLength * 4;
            _decoder    = isFile ? null : Utf8.GetDecoder();
            _ct         = ct;
            StartOffset = cursor.Position;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _failure?.Throw();
            using var linked    = ct.CanBeCanceled && ct != _ct ? CancellationTokenSource.CreateLinkedTokenSource(_ct, ct) : null;
            var       readToken = linked?.Token ?? _ct;
            readToken.ThrowIfCancellationRequested();
            if (Interlocked.Exchange(ref _reading, 1) != 0)
            {
                throw new InvalidOperationException("Multipart sections must be read sequentially.");
            }

            try
            {
                if (_ended || buffer.IsEmpty)
                {
                    return 0;
                }

                var (count, final) = await _cursor.ReadPartAsync(buffer, _delimiter, _options.MaxMultipartHeaderBytes, readToken);
                if (count > _maxBytes - BytesRead)
                {
                    throw Limit();
                }

                BytesRead += count;
                ValidateField(buffer.Span[..count], final is not null);
                _ended = final is not null;
                Final  = final == true;
                return count;
            }
            catch (Exception exception)
            {
                _failure = ExceptionDispatchInfo.Capture(exception);
                throw;
            }
            finally
            {
                Volatile.Write(ref _reading, 0);
            }
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("Read multipart bodies asynchronously.");

        public override void Flush() => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private void ValidateField(ReadOnlySpan<byte> bytes, bool final)
        {
            if (_decoder is null)
            {
                return;
            }

            Span<char> characters = stackalloc char[512];
            bool       complete;
            do
            {
                _decoder.Convert(bytes, characters, final, out var used, out var written, out complete);
                bytes       =  bytes[used..];
                _characters += written;
                if (_characters > _options.MaxValueLength)
                {
                    throw Limit();
                }
            } while (!complete);
        }

        protected override void Dispose(bool disposing)
        {
            _disposed = true;
            base.Dispose(disposing);
        }
    }
}
