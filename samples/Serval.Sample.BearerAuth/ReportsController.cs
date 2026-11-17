#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Serval.Authentication.Authorization;
using Serval.Http;
using Serval.Http.Constants;
using Serval.Routing;

namespace Serval.Sample.BearerAuth;

[Controller]
public sealed class ReportsController : ControllerBase
{
    private static readonly string[] AvailableReports = ["Monthly summary"];

    [Get("/")]
    public ValueTask<IActionResult> IndexAsync(RequestContext context) => TextAsync("""
                                                                                    Bearer authentication and authorization policies (loopback demo)
                                                                                    Send Authorization: Bearer reader-demo (bob) or Bearer editor-demo (alice).
                                                                                    GET /reports          Requires the reports.read claim; either token works.
                                                                                    POST /reports/preview Requires the editor role and reports.write claim.
                                                                                    GET /reports/bob      Requires a matching route owner; reader-demo works.
                                                                                    GET /reports/alice    Requires a matching route owner; editor-demo works.
                                                                                    GET /documents/1      Typed View permission; bob owns this document.
                                                                                    GET /documents/2      Alice's approved report; reader-demo is denied.
                                                                                    POST /documents/2/edit-preview -> 403 even for editor-demo: the lock voter vetoes.
                                                                                    POST /documents/3/edit-preview -> 200 for editor-demo; no changes are saved.
                                                                                    DocumentPermission.View/Edit are enum values, not permission-name strings.
                                                                                    The document actions explicitly authenticate, load, authorize, and respond.
                                                                                    Missing/invalid tokens return 401 with WWW-Authenticate: Bearer.
                                                                                    Authenticated users without permission get 403.
                                                                                    Tokens are public fixtures, not JWTs or production credentials.
                                                                                    """);

    [Get("/reports")]
    [Authorize(Policy = "reports.read")]
    public ValueTask<IActionResult> ListAsync(RequestContext context)
    {
        context.Response.Headers[HeaderNames.CacheControl] = "no-store";
        return JsonAsync(new
        {
            user    = context.User.Identity?.Name,
            reports = AvailableReports
        });
    }

    [Post("/reports/preview")]
    [Authorize(Policy = "reports.write")]
    public ValueTask<IActionResult> PreviewAsync(RequestContext context)
    {
        context.Response.Headers[HeaderNames.CacheControl] = "no-store";
        return JsonAsync(new
        {
            author  = context.User.Identity?.Name,
            message = "The write policy passed. No report was saved."
        });
    }

    [Get("/reports/{owner:alpha}")]
    [Authorize(Policy = "reports.owner")]
    public ValueTask<IActionResult> OwnerAsync(RequestContext context)
    {
        context.Response.Headers[HeaderNames.CacheControl] = "no-store";
        return JsonAsync(new
        {
            owner  = context.RouteValues["owner"],
            report = "Private summary"
        });
    }
}
