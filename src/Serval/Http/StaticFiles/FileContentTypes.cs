#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Collections.Frozen;
using Serval.Http.Constants;

namespace Serval.Http.StaticFiles;

/// <summary>Default media types for common file extensions.</summary>
public static class FileContentTypes
{
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

    /// <summary>Looks up a filename's extension, returning null for an unknown type.</summary>
    public static string? Get(string path) => ContentTypes.GetValueOrDefault(Path.GetExtension(path));
}
