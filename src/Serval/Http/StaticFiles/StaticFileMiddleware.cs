#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Serval.Http.Constants;
using Serval.Middleware;

namespace Serval.Http.StaticFiles;

/// <summary>Composes safe path resolution, media types, cache policy, and file response writing.</summary>
public sealed class StaticFileMiddleware : IMiddleware
{
    private readonly StaticFileOptions _options;
    private readonly StaticFileResolver? _resolver;

    /// <summary>Creates an independently configured file mount. Invoke it with a relative request path when mounting below a prefix.</summary>
    public StaticFileMiddleware(StaticFileOptions options, TrailingSlashBehavior trailingSlashes = TrailingSlashBehavior.Ignore)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
        if (options.DirectoryPath is {} directory)
        {
            _resolver = new StaticFileResolver(directory, options.ServeDotFiles, trailingSlashes != TrailingSlashBehavior.Strict);
        }
    }

    /// <inheritdoc />
    public async ValueTask InvokeAsync(RequestContext context, RequestDelegate next)
    {
        if (!await TryServeAsync(context))
        {
            await next(context);
        }
    }

    /// <summary>Serves an eligible GET or HEAD file. Fallback decisions remain with the caller.</summary>
    public async ValueTask<bool> TryServeAsync(RequestContext context, string? requestPath = null)
    {
        if (_resolver is null || context.Request.Method is not (HttpMethods.Get or HttpMethods.Head))
        {
            return false;
        }

        var path = _resolver.Resolve(requestPath ?? context.Request.Url.AbsolutePath);
        if (path is null)
        {
            return false;
        }

        var contentType = _options.ContentTypeResolver(path);
        if (contentType is null && !_options.ServeUnknownFileTypes)
        {
            return false;
        }

        try
        {
            var file = new FileResource(path);
            await FileResponse.WriteAsync(context, file, contentType, _options.CachePolicy?.Invoke(file) ?? _options.CacheControl);
            return true;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }
}
