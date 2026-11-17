#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Globalization;
using System.Net.Http.Headers;
using Serval.Http.Constants;

namespace Serval.Http.StaticFiles;

/// <summary>Evaluates HTTP cache validators independently of file storage and response writing.</summary>
public static class ConditionalRequests
{
    /// <summary>Evaluates If-None-Match before If-Modified-Since for GET or HEAD. Other methods return false.</summary>
    public static bool IsNotModified(Request request, string etag, DateTimeOffset modified)
    {
        if (request.Method is not (HttpMethods.Get or HttpMethods.Head))
        {
            return false;
        }

        var ifNoneMatch = request.Headers[HeaderNames.IfNoneMatch];
        if (ifNoneMatch is not null)
        {
            var tag = etag.StartsWith("W/", StringComparison.Ordinal) ? etag[2..] : etag;
            return Matches(ifNoneMatch.AsSpan(), tag);
        }

        var since = request.Headers[HeaderNames.IfModifiedSince];
        return since is not null && DateTimeOffset.TryParse(since, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var timestamp) && modified.ToUnixTimeSeconds() <= timestamp.ToUnixTimeSeconds();
    }

    private static bool Matches(ReadOnlySpan<char> values, string tag)
    {
        values = values.Trim();
        if (values.SequenceEqual("*"))
        {
            return true;
        }

        var matched = false;
        while (!values.IsEmpty)
        {
            // Commas inside the quoted opaque tag are data, not list separators.
            var start = values.StartsWith("W/", StringComparison.Ordinal) ? 2 : 0;
            if (values.Length <= start || values[start] != '"')
            {
                return false;
            }

            var closing = values[(start + 1)..].IndexOf('"');
            if (closing < 0)
            {
                return false;
            }

            var length    = start + closing + 2;
            var candidate = values[..length].ToString();
            if (!EntityTagHeaderValue.TryParse(candidate, out var parsed))
            {
                return false;
            }

            matched |= parsed.Tag == tag;
            values  =  values[length..].TrimStart();
            if (values.IsEmpty)
            {
                return matched;
            }

            if (values[0] != ',')
            {
                return false;
            }

            values = values[1..].TrimStart();
            if (values.IsEmpty)
            {
                return false;
            }
        }

        return false;
    }
}

/// <summary>Writes file metadata, conditional responses, and file contents after application authorization.</summary>
public static class FileResponse
{
    /// <summary>Writes a file response, supporting HEAD and conditional GET. A null cache policy leaves the existing header alone.</summary>
    public static async ValueTask WriteAsync(RequestContext context,
                                             FileResource   file,
                                             string?        contentType  = null,
                                             string?        cacheControl = null,
                                             string?        downloadName = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(file);

        var response = context.Response;
        contentType                                       ??= MimeTypes.OctetStream;
        response.Headers[HeaderNames.ContentType]         =   contentType;
        response.Headers[HeaderNames.LastModified]        =   file.LastModified.ToString("R", CultureInfo.InvariantCulture);
        response.Headers[HeaderNames.ETag]                =   file.ETag;
        response.Headers[HeaderNames.XContentTypeOptions] =   "nosniff";
        if (cacheControl is not null)
        {
            response.Headers[HeaderNames.CacheControl] = cacheControl;
        }

        if (downloadName is not null)
        {
            if (downloadName.Length == 0 || downloadName.Any(character => character is < ' ' or '\x7f'))
            {
                throw new ArgumentException("Invalid download filename.", nameof(downloadName));
            }

            response.Headers[HeaderNames.ContentDisposition] = new ContentDispositionHeaderValue(ContentDispositions.Attachment)
            {
                FileNameStar = downloadName,
            }.ToString();
        }

        if (ConditionalRequests.IsNotModified(context.Request, file.ETag, file.LastModified))
        {
            response.StatusCode = StatusCodes.NotModified;
            return;
        }

        if (context.Request.Method == HttpMethods.Head)
        {
            response.Headers[HeaderNames.ContentLength] = file.Length.ToString(CultureInfo.InvariantCulture);
            await response.StartAsync(context.RequestAborted);
            return;
        }

        await response.WriteFileAsync(file.Path, contentType, cancellationToken: context.RequestAborted);
    }
}
