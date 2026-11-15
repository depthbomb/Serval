#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.Extensions.DependencyInjection;
using Serval.Binding;
using Serval.Generated;
using Serval.Http;
using Serval.Http.Constants;
using Serval.Routing;
using Serval.SignedUrls;

namespace Serval.Sample.SignedUrls;

[Controller]
public sealed class DownloadsController : ControllerBase
{
    private readonly TimeProvider      _clock;
    private readonly ISignedUrlService _signedUrls;

    public DownloadsController(ISignedUrlService signedUrls, TimeProvider clock)
    {
        _signedUrls = signedUrls;
        _clock      = clock;
    }

    [Get("/")]
    public ValueTask<IActionResult> IndexAsync() => TextAsync("""
                                                              Signed URLs (loopback demo)
                                                              GET /links                 Creates permanent, temporary, typed-helper, and inspection links.
                                                              GET /links?seconds=5       Makes the expiring links last five seconds; the default is 30.
                                                              Follow a returned URL by appending it to this server's origin.
                                                              GET /downloads/42          Returns 403 without a valid signature.
                                                              GET /inspect/42            Returns a validation failure without a valid signature.

                                                              The download links return a JSON preview of a report.
                                                              Change the report ID or a query value in a signed URL to get 403.
                                                              Wait for a temporary link to expire and request it again to get 403.
                                                              Inspection links return verified route/query data or the reason validation failed.

                                                              This demo lets anyone create links to sample reports.
                                                              Tokens contain readable data and can be reused until they expire.
                                                              Without SignedUrls:Key configuration, restarting creates a new key and invalidates all links.
                                                              Set SignedUrls__Key to a base64 secret of at least 32 random bytes to keep links across restarts.
                                                              """);

    [Get("/links")]
    public ValueTask<IActionResult> LinksAsync([FromQuery] int seconds = 30)
    {
        AbortIf(seconds is < 1 or > 3600, StatusCodes.BadRequest, "Seconds must be between 1 and 3600.");
        Response.Headers[HeaderNames.CacheControl] = CacheControlDirectives.NoStore;

        var values = new Dictionary<string, object?>
        {
            ["id"] = 42,
        };
        var query = new Dictionary<string, object?>
        {
            ["format"] = "pdf",
            ["title"]  = "Monthly report",
        };
        var lifetime   = TimeSpan.FromSeconds(seconds);
        var expiresAt  = _clock.GetUtcNow().Add(lifetime);
        var permanent  = _signedUrls.Path("downloads.show", values, query);
        var temporary  = _signedUrls.TemporaryPath("downloads.show", lifetime, values, query);
        var typed      = _signedUrls.Sign(Urls.DownloadsShow(42, query), expiresAt);
        var inspection = _signedUrls.Path("downloads.inspect", values, query, expiresAt);
        return JsonAsync(new
        {
            permanent,
            temporary,
            typed,
            inspection,
            seconds,
        });
    }

    [Get("/downloads/{id:int:min(1)}", Name = "downloads.show")]
    [RequireSignedUrl]
    public ValueTask<IActionResult> DownloadAsync([FromRoute] int id, [FromQuery] string format, [FromQuery] string title)
    {
        // Middleware checks the signature and expiry before the action binds or uses any values.
        Response.Headers[HeaderNames.CacheControl] = CacheControlDirectives.NoStore;
        return JsonAsync(new
        {
            id,
            format,
            title,
        });
    }

    [Get("/inspect/{id:int:min(1)}", Name = "downloads.inspect")]
    public ValueTask<IActionResult> InspectAsync()
    {
        // Resolve from the request container when constructor injection is inconvenient.
        var signedUrls = Services.GetRequiredService<ISignedUrlService>();
        var result     = signedUrls.Validate(Request);
        Response.Headers[HeaderNames.CacheControl] = CacheControlDirectives.NoStore;
        return JsonAsync(new
        {
            result.IsValid,
            failure = result.Failure.ToString(),
            result.Data,
        }, statusCode: result.IsValid ? StatusCodes.Ok : StatusCodes.Forbidden);
    }
}
