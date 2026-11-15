#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

namespace Serval.Http.Constants;

/// <summary>Common media types for response headers.</summary>
/// <remarks>Text, Html, Csv, and Markdown include a UTF-8 charset parameter. Other values contain only the media type.</remarks>
public static class MimeTypes
{
    /// <summary>The <c>application/json</c> media type.</summary>
    public const string Json = "application/json";

    /// <summary>The <c>application/problem+json</c> media type.</summary>
    public const string ProblemJson = "application/problem+json";

    /// <summary>The <c>text/plain</c> media type with UTF-8 encoding.</summary>
    public const string Text = "text/plain; charset=utf-8";

    /// <summary>The <c>text/html</c> media type with UTF-8 encoding.</summary>
    public const string Html = "text/html; charset=utf-8";

    /// <summary>The <c>text/event-stream</c> media type.</summary>
    public const string EventStream = "text/event-stream";

    /// <summary>The <c>application/octet-stream</c> media type.</summary>
    public const string OctetStream = "application/octet-stream";

    /// <summary>The <c>application/x-www-form-urlencoded</c> media type.</summary>
    public const string FormUrlEncoded = "application/x-www-form-urlencoded";

    /// <summary>The <c>multipart/form-data</c> media type.</summary>
    public const string MultipartFormData = "multipart/form-data";

    /// <summary>The <c>application/xml</c> media type.</summary>
    public const string Xml = "application/xml";

    /// <summary>The <c>application/pdf</c> media type.</summary>
    public const string Pdf = "application/pdf";

    /// <summary>The <c>image/png</c> media type.</summary>
    public const string Png = "image/png";

    /// <summary>The <c>image/jpeg</c> media type.</summary>
    public const string Jpeg = "image/jpeg";

    /// <summary>The <c>image/svg+xml</c> media type.</summary>
    public const string Svg = "image/svg+xml";

    /// <summary>The <c>image/webp</c> media type.</summary>
    public const string Webp = "image/webp";

    /// <summary>The <c>text/css</c> media type.</summary>
    public const string Css = "text/css";

    /// <summary>The <c>text/javascript</c> media type.</summary>
    public const string JavaScript = "text/javascript";

    /// <summary>The <c>image/gif</c> media type.</summary>
    public const string Gif = "image/gif";

    /// <summary>The <c>image/x-icon</c> media type.</summary>
    public const string Icon = "image/x-icon";

    /// <summary>The <c>image/avif</c> media type.</summary>
    public const string Avif = "image/avif";

    /// <summary>The <c>font/woff</c> media type.</summary>
    public const string Woff = "font/woff";

    /// <summary>The <c>font/woff2</c> media type.</summary>
    public const string Woff2 = "font/woff2";

    /// <summary>The <c>application/wasm</c> media type.</summary>
    public const string WebAssembly = "application/wasm";

    /// <summary>The <c>application/manifest+json</c> media type.</summary>
    public const string WebManifest = "application/manifest+json";

    /// <summary>The <c>text/csv</c> media type with UTF-8 encoding.</summary>
    public const string Csv = "text/csv; charset=utf-8";

    /// <summary>The <c>audio/mpeg</c> media type.</summary>
    public const string Mp3 = "audio/mpeg";

    /// <summary>The <c>video/mp4</c> media type.</summary>
    public const string Mp4 = "video/mp4";

    /// <summary>The <c>video/webm</c> media type.</summary>
    public const string Webm = "video/webm";

    /// <summary>The <c>application/zip</c> media type.</summary>
    public const string Zip = "application/zip";

    /// <summary>JSON Patch documents containing an ordered list of operations.</summary>
    public const string JsonPatch = "application/json-patch+json";

    /// <summary>JSON Merge Patch documents describing changes to an object.</summary>
    public const string JsonMergePatch = "application/merge-patch+json";

    /// <summary>Problem details encoded as XML.</summary>
    public const string ProblemXml = "application/problem+xml";

    /// <summary>XHTML documents parsed as XML.</summary>
    public const string Xhtml = "application/xhtml+xml";

    /// <summary>YAML documents.</summary>
    public const string Yaml = "application/yaml";

    /// <summary>Markdown text with UTF-8 encoding.</summary>
    public const string Markdown = "text/markdown; charset=utf-8";

    /// <summary>iCalendar event and calendar data.</summary>
    public const string Calendar = "text/calendar";

    /// <summary>WebVTT captions and timed text.</summary>
    public const string WebVtt = "text/vtt";

    /// <summary>Rich Text Format documents.</summary>
    public const string Rtf = "application/rtf";

    /// <summary>Atom feeds.</summary>
    public const string Atom = "application/atom+xml";

    /// <summary>RSS feeds.</summary>
    public const string Rss = "application/rss+xml";

    /// <summary>Windows bitmap images.</summary>
    public const string Bmp = "image/bmp";

    /// <summary>TIFF images.</summary>
    public const string Tiff = "image/tiff";

    /// <summary>HEIC images.</summary>
    public const string Heic = "image/heic";

    /// <summary>TrueType fonts.</summary>
    public const string Ttf = "font/ttf";

    /// <summary>OpenType fonts.</summary>
    public const string Otf = "font/otf";

    /// <summary>AAC audio.</summary>
    public const string Aac = "audio/aac";

    /// <summary>FLAC lossless audio.</summary>
    public const string Flac = "audio/flac";

    /// <summary>WAVE audio, using the widely supported <c>audio/wav</c> spelling.</summary>
    public const string Wav = "audio/wav";

    /// <summary>Audio in an MP4 container, commonly stored in .m4a files.</summary>
    public const string Mp4Audio = "audio/mp4";

    /// <summary>Audio in an Ogg container, including Ogg Opus files.</summary>
    public const string OggAudio = "audio/ogg";

    /// <summary>Video in an Ogg container.</summary>
    public const string OggVideo = "video/ogg";

    /// <summary>Audio in a WebM container.</summary>
    public const string WebmAudio = "audio/webm";

    /// <summary>MPEG video.</summary>
    public const string Mpeg = "video/mpeg";

    /// <summary>A gzip file; this media type alone does not apply HTTP content encoding.</summary>
    public const string Gzip = "application/gzip";

    /// <summary>Microsoft Word Open XML documents.</summary>
    public const string Docx = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    /// <summary>Microsoft Excel Open XML workbooks.</summary>
    public const string Xlsx = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    /// <summary>Microsoft PowerPoint Open XML presentations.</summary>
    public const string Pptx = "application/vnd.openxmlformats-officedocument.presentationml.presentation";
}
