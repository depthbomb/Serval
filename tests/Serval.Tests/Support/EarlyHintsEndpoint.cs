#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Serval.Http;
using Serval.Http.Constants;
using Serval.Http.Results;

namespace Serval.Tests.Support;

internal sealed class EarlyHintsEndpoint
{
    public const string               Stylesheet = "</site.css>; rel=preload; as=style";
    public const string               Script     = "</main.js>; rel=modulepreload";
    public const string               Font       = "</font.woff2>; rel=preload; as=font; crossorigin";
    public       TaskCompletionSource Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async ValueTask<IActionResult> InvokeAsync(RequestContext context)
    {
        context.Response.Headers["X-Final"] = "only";
        var hints = new Headers();
        hints.Append(HeaderNames.Link, Stylesheet);
        hints.Append(HeaderNames.Link, Script);
        Assert.True(context.Response.SupportsEarlyHints);
        Assert.True(await context.Response.SendEarlyHintsAsync(hints, context.RequestAborted));
        hints[HeaderNames.Link] = Font;
        Assert.True(await context.Response.SendEarlyHintsAsync(hints, context.RequestAborted));
        Assert.False(context.Response.HasStarted);

        // The client must receive both hints before it releases the final response.
        await Finish.Task.WaitAsync(context.RequestAborted);
        if (context.Request.Query.ContainsKey("fail"))
        {
            throw new HttpErrorException(StatusCodes.UnprocessableContent, "hinted failure");
        }

        return new TextResult("complete");
    }
}
