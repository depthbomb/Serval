#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Buffers;
using System.Collections.Frozen;
using System.Globalization;
using Serval.Http.Constants;

namespace Serval.Http.StaticFiles;

internal sealed class StaticFileServer
{
    private readonly        ServalOptions      _options;
    private readonly        string?            _root;
    private static readonly SearchValues<char> InvalidFileNameCharacters = SearchValues.Create(Path.GetInvalidFileNameChars());
    private static readonly FrozenDictionary<string, string> ContentTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [".txt"]         = MimeTypes.Text,
        [".html"]        = MimeTypes.Html,
        [".htm"]         = MimeTypes.Html,
        [".css"]         = MimeTypes.Css,
        [".js"]          = MimeTypes.JavaScript,
        [".mjs"]         = MimeTypes.JavaScript,
        [".json"]        = MimeTypes.Json,
        [".map"]         = MimeTypes.Json,
        [".xml"]         = MimeTypes.Xml,
        [".svg"]         = MimeTypes.Svg,
        [".png"]         = MimeTypes.Png,
        [".jpg"]         = MimeTypes.Jpeg,
        [".jpeg"]        = MimeTypes.Jpeg,
        [".webp"]        = MimeTypes.Webp,
        [".avif"]        = MimeTypes.Avif,
        [".gif"]         = MimeTypes.Gif,
        [".ico"]         = MimeTypes.Icon,
        [".pdf"]         = MimeTypes.Pdf,
        [".csv"]         = MimeTypes.Csv,
        [".woff"]        = MimeTypes.Woff,
        [".woff2"]       = MimeTypes.Woff2,
        [".wasm"]        = MimeTypes.WebAssembly,
        [".webmanifest"] = MimeTypes.WebManifest,
        [".mp3"]         = MimeTypes.Mp3,
        [".mp4"]         = MimeTypes.Mp4,
        [".webm"]        = MimeTypes.Webm,
        [".zip"]         = MimeTypes.Zip,
        [".bin"]         = MimeTypes.OctetStream,
        [".xhtml"]       = MimeTypes.Xhtml,
        [".yaml"]        = MimeTypes.Yaml,
        [".yml"]         = MimeTypes.Yaml,
        [".md"]          = MimeTypes.Markdown,
        [".ics"]         = MimeTypes.Calendar,
        [".vtt"]         = MimeTypes.WebVtt,
        [".rtf"]         = MimeTypes.Rtf,
        [".atom"]        = MimeTypes.Atom,
        [".rss"]         = MimeTypes.Rss,
        [".bmp"]         = MimeTypes.Bmp,
        [".tif"]         = MimeTypes.Tiff,
        [".tiff"]        = MimeTypes.Tiff,
        [".heic"]        = MimeTypes.Heic,
        [".ttf"]         = MimeTypes.Ttf,
        [".otf"]         = MimeTypes.Otf,
        [".aac"]         = MimeTypes.Aac,
        [".flac"]        = MimeTypes.Flac,
        [".wav"]         = MimeTypes.Wav,
        [".m4a"]         = MimeTypes.Mp4Audio,
        [".ogg"]         = MimeTypes.OggAudio,
        [".oga"]         = MimeTypes.OggAudio,
        [".opus"]        = MimeTypes.OggAudio,
        [".ogv"]         = MimeTypes.OggVideo,
        [".weba"]        = MimeTypes.WebmAudio,
        [".mpeg"]        = MimeTypes.Mpeg,
        [".mpg"]         = MimeTypes.Mpeg,
        [".gz"]          = MimeTypes.Gzip,
        [".docx"]        = MimeTypes.Docx,
        [".xlsx"]        = MimeTypes.Xlsx,
        [".pptx"]        = MimeTypes.Pptx,
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    public StaticFileServer(ServalOptions options)
    {
        _options = options;
        options.StaticFiles.Validate();
        if (options.StaticFiles.DirectoryPath is { } directory)
        {
            var info = new DirectoryInfo(Path.GetFullPath(directory));
            _root = Path.TrimEndingDirectorySeparator(info.ResolveLinkTarget(true)?.FullName ?? info.FullName);
        }
    }

    public ValueTask<bool> TryServeAsync(RequestContext context)
        => TryServeAsync(context, context.Request.Url.AbsolutePath);

    public ValueTask<bool> TryServeFallbackAsync(RequestContext context)
        => _options.StaticFiles.FallbackToIndexHtml ? TryServeAsync(context, "/index.html") : ValueTask.FromResult(false);

    private async ValueTask<bool> TryServeAsync(RequestContext context, string requestPath)
    {
        if (_root is null || context.Request.Method is not (HttpMethods.Get or HttpMethods.Head))
        {
            return false;
        }

        var path = FindFile(requestPath);
        if (path is null)
        {
            return false;
        }

        var contentType = ContentTypes.GetValueOrDefault(Path.GetExtension(path));
        if (contentType is null && !_options.StaticFiles.ServeUnknownFileTypes)
        {
            return false;
        }

        try
        {
            var info     = new FileInfo(path);
            var modified = info.LastWriteTimeUtc;
            var length   = info.Length;
            // Metadata is cheap to check, but isn't a byte-for-byte content identity, so this is a weak ETag.
            var etag     = $"W/\"{length:x}-{modified.Ticks:x}\"";
            var response = context.Response;
            response.Headers[HeaderNames.ContentType]         = contentType ?? MimeTypes.OctetStream;
            response.Headers[HeaderNames.LastModified]        = modified.ToString("R", CultureInfo.InvariantCulture);
            response.Headers[HeaderNames.ETag]                = etag;
            response.Headers[HeaderNames.CacheControl]        = _options.StaticFiles.CacheControl;
            response.Headers[HeaderNames.XContentTypeOptions] = "nosniff";

            if (NotModified(context.Request, etag, modified))
            {
                response.StatusCode = StatusCodes.NotModified;
                return true;
            }

            if (context.Request.Method == HttpMethods.Head)
            {
                response.Headers[HeaderNames.ContentLength] = length.ToString(CultureInfo.InvariantCulture);
                await response.StartAsync(context.RequestAborted);
            }
            else
            {
                await response.WriteFileAsync(path, contentType ?? MimeTypes.OctetStream, cancellationToken: context.RequestAborted);
            }

            return true;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    private string? FindFile(string requestPath)
    {
        var segments = requestPath.TrimStart('/').Split('/').Select(Uri.UnescapeDataString).ToArray();
        if (segments[^1].Length == 0 && _options.TrailingSlashes != TrailingSlashBehavior.Strict)
        {
            segments = segments[..^1];
        }

        if (segments.Length == 0)
        {
            return null;
        }

        var current = _root!;
        try
        {
            for (var index = 0; index < segments.Length; index++)
            {
                var segment = segments[index];
                var invalid = segment.Length == 0 || segment is "." or ".." || segment.AsSpan().ContainsAny(InvalidFileNameCharacters) || segment.AsSpan().ContainsAnyInRange('\0', '\x1f') || segment.AsSpan().ContainsAny('/', '\\', ':');

                // Win32 strips trailing dots and spaces, which can turn an allowed segment into '..'.
                invalid |= OperatingSystem.IsWindows() && (segment.EndsWith('.') || segment.EndsWith(' '));
                if (invalid || !_options.StaticFiles.ServeDotFiles && segment.StartsWith('.'))
                {
                    return null;
                }

                current = Path.Combine(current, segment);

                var attributes = File.GetAttributes(current);
                // Check every segment: a link in an intermediate directory could escape the public root.
                if ((attributes & FileAttributes.ReparsePoint) != 0 || !_options.StaticFiles.ServeDotFiles && (attributes & FileAttributes.Hidden) != 0)
                {
                    return null;
                }

                var directory = (attributes & FileAttributes.Directory) != 0;
                if (directory != index < segments.Length - 1)
                {
                    return null;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }

        return current;
    }

    private static bool NotModified(Request request, string etag, DateTime modified)
    {
        var ifNoneMatch = request.Headers[HeaderNames.IfNoneMatch];
        // If-None-Match takes precedence, even when If-Modified-Since would otherwise match.
        if (ifNoneMatch is not null)
        {
            var tag = etag[2..];
            return ifNoneMatch.Split(',').Select(value => value.Trim()).Any(value => value == "*" || (value.StartsWith("W/", StringComparison.Ordinal) ? value[2..] : value) == tag);
        }

        var since   = request.Headers[HeaderNames.IfModifiedSince];
        var rounded = new DateTimeOffset(modified).ToUnixTimeSeconds();
        return since is not null && DateTimeOffset.TryParse(since, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var timestamp) && rounded <= timestamp.ToUnixTimeSeconds();
    }
}
