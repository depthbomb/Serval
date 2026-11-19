#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.Extensions.DependencyInjection;
using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Json;
using Serval.Binding;
using Serval.Http;
using Serval.Http.Constants;
using Serval.Routing;
using Serval.Tests.Support;

namespace Serval.Tests.Core.Routing;

public sealed class ControllerBaseTests
{
    [Fact]
    public async Task GeneratedActionsExposeBoundValuesAndIsolateRequestPropertiesAcrossAwaits()
    {
        var             capture   = new ControllerCapture();
        await using var app       = new TestApplication(configureServices: services => services.AddSingleton(capture));
        var             responses = await Task.WhenAll(Enumerable.Range(1, 12).Select(id => app.SendAsync($"/controller-base/info/{id}?tag=request-{id}")));
        for (var index = 0; index < responses.Length; index++)
        {
            var response = responses[index];
            Assert.Equal(200, response.StatusCode);
            using var json = JsonDocument.Parse(response.Text);
            Assert.Equal(index + 1, json.RootElement.GetProperty("id").GetInt32());
            Assert.Equal($"request-{index               + 1}", json.RootElement.GetProperty("user").GetString());
            Assert.Equal($"/controller-base/info/{index + 1}", json.RootElement.GetProperty("url").GetString());
            Assert.Equal("yes", response.Headers["X-Context"]);
        }

        Assert.Equal(12, capture.Controllers.Distinct().Count());
        Assert.All(capture.Controllers, controller => Assert.Throws<InvalidOperationException>(() => controller.Context));
    }

    [Theory]
    [InlineData("success", 200)]
    [InlineData("action", 409)]
    [InlineData("result", 422)]
    public async Task ContextLastsThroughDeferredResultsAndClearsAfterSuccessOrFailure(string mode, int status)
    {
        var             capture  = new ControllerCapture();
        await using var app      = new TestApplication(configureServices: services => services.AddSingleton(capture));
        var             response = await app.SendAsync("/controller-base/deferred/" + mode);
        Assert.Equal(status, response.StatusCode);
        var controller = Assert.Single(capture.Controllers);
        Assert.Equal(mode != "action", controller.ResultSawContext);
        Assert.Throws<InvalidOperationException>(() => controller.Context);
        Assert.Contains(mode == "success" ? "deferred" : "failed", response.Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManualActionsRejectConcurrentInstanceReuseAndClearContextOnCancellation(bool cancel)
    {
        var controller = new ManualController();
        Assert.Throws<InvalidOperationException>(() => controller.Context);
        await using var app = new TestApplication(
            configureServices: services => services.AddSingleton(controller),
            configureBuilder: builder => builder.AddController<ManualController>("GET", "/manual-base/{id:int}", null, (instance, _) => instance.InvokeAsync()));
        using var ct    = new CancellationTokenSource();
        var       first = app.SendAsync("/manual-base/1", cancellationToken: ct.Token);
        await controller.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var context = controller.Context;
        try
        {
            var rejected = await app.SendAsync("/manual-base/2");
            Assert.Equal(500, rejected.StatusCode);
            Assert.Same(context, controller.Context);
            if (cancel)
            {
                await ct.CancelAsync();
            }
        }
        finally
        {
            controller.Release.TrySetResult();
        }

        var completed = await first.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Throws<InvalidOperationException>(() => controller.Context);
        if (cancel)
        {
            Assert.True(completed.Aborted);
        }
        else
        {
            Assert.Equal("1", completed.Text);
        }

        Assert.Equal("3", (await app.SendAsync("/manual-base/3")).Text);
        Assert.Throws<InvalidOperationException>(() => controller.Context);
    }

    private sealed class ManualController : ControllerBase
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<IActionResult> InvokeAsync()
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(RequestAborted);
            RequestAborted.ThrowIfCancellationRequested();
            return Text(RouteValue<int>("id").ToString());
        }
    }
}

internal sealed class ControllerCapture
{
    public ConcurrentBag<BaseHelperController> Controllers { get; } = [];
}

[Controller]
[Route("/controller-base")]
public sealed class BaseHelperController : ControllerBase
{
    internal bool ResultSawContext { get; private set; }

    [Get("/info/{id:int}", Name = "controller-base.info")]
    public async ValueTask<IActionResult> InfoAsync([FromRoute] int id, [FromQuery] string tag, CancellationToken ct)
    {
        Services.GetRequiredService<ControllerCapture>().Controllers.Add(this);
        Assert.Same(Context.Request, Request);
        Assert.Same(Context.Response, Response);
        Assert.Same(Context.Services, Services);
        Assert.Equal(ct, RequestAborted);
        Assert.Same(Context.RouteValues, RouteValues);
        Assert.Same(Context.Items, Items);
        Assert.Equal(Context.TraceId, TraceId);
        Assert.NotEmpty(TraceId);
        User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, tag)], "test"));
        Assert.Same(Context.User, User);
        Items["tag"] = tag;
        DisableRequestTimeout();
        await Task.Yield();
        Assert.Equal(tag, Items["tag"]);
        Response.Headers["X-Context"] = "yes";
        AbortUnless(id == RouteValue<int>("id"), StatusCodes.BadRequest);
        return Json(new
        {
            id,
            user = User.Identity!.Name,
            url = Urls.Path("controller-base.info", new Dictionary<string, object?>
            {
                ["id"] = id
            })
        });
    }

    [Get("/deferred/{mode}")]
    public ValueTask<IActionResult> DeferredAsync([FromRoute] string mode)
    {
        Services.GetRequiredService<ControllerCapture>().Controllers.Add(this);
        AbortIf(mode == "action", StatusCodes.Conflict, "action failed");
        return ValueTask.FromResult<IActionResult>(new DeferredResult(this, mode));
    }

    private sealed class DeferredResult(BaseHelperController controller, string mode) : IActionResult
    {
        public async ValueTask ExecuteAsync(RequestContext context)
        {
            await Task.Yield();
            Assert.Same(context, controller.Context);
            controller.ResultSawContext = true;
            if (mode == "result")
            {
                controller.Abort(StatusCodes.UnprocessableContent, "result failed");
            }

            await controller.Text("deferred").ExecuteAsync(context);
        }
    }
}
