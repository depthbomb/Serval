namespace Serval.Http.StaticFiles;

internal sealed class StaticFileServer
{
    private readonly StaticFileMiddleware _middleware;
    private readonly StaticFileOptions _options;

    public StaticFileServer(ServalOptions options)
    {
        _options = options.StaticFiles;
        _middleware = new StaticFileMiddleware(_options, options.TrailingSlashes);
    }

    public ValueTask<bool> TryServeAsync(RequestContext context) => _middleware.TryServeAsync(context);

    public ValueTask<bool> TryServeFallbackAsync(RequestContext context)
        => _options.FallbackToIndexHtml ? _middleware.TryServeAsync(context, "/index.html") : ValueTask.FromResult(false);
}
