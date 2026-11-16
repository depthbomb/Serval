#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Serval.Authentication.Cookies;
using Serval.Binding;
using Serval.Http;
using Serval.Http.Constants;
using Serval.Routing;

namespace Serval.Tests.Support;

[Controller]
[Route("query")]
public sealed class QueryController : ControllerBase
{
    [Query("search")]
    [RequireCsrf]
    public ValueTask<IActionResult> SearchAsync([FromBody] Message query, RequestContext context)
    {
        context.Response.Headers[HeaderNames.AcceptQuery] = "\"" + MimeTypes.Json + "\"";
        return TextAsync(query.Text);
    }

    [HttpRoute(HttpMethods.Query, "custom")]
    public async ValueTask<IActionResult> CustomAsync(RequestContext context)
    {
        var query = await context.Request.ReadJsonAsync(TestJsonContext.Default.Message, context.RequestAborted);
        return Text(query.Text);
    }

    [Query("form")]
    public ValueTask<IActionResult> FormAsync([FromForm] string term) => TextAsync(term);

    [Query("raw")]
    public ValueTask<IActionResult> RawAsync() => TextAsync("raw");
}
