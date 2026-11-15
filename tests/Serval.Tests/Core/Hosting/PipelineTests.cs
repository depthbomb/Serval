#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using Serval.Hosting;
using Serval.Http.Constants;
using Serval.Tests.Support;

namespace Serval.Tests.Core.Hosting;

public sealed class PipelineTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResultsExecuteBeforeMiddlewareAndRequestScopeAreDisposed(bool fail)
    {
        var             lifetime = new MiddlewareLifetime();
        await using var app      = new TestApplication(configureServices: services => services.AddSingleton(lifetime));
        var             response = await app.SendAsync(fail ? "/test/result-lifetime-error" : "/test/result-lifetime");
        Assert.Equal(fail ? StatusCodes.InternalServerError : StatusCodes.Ok, response.StatusCode);
        if (fail)
        {
            Assert.DoesNotContain("Result execution failed", response.Text);
        }
        else
        {
            Assert.Equal("completed", response.Text);
        }

        string[] expected = fail
            ? ["outer enter", "async enter", "result", "async dispose", "outer dispose"]
            : ["outer enter", "async enter", "result", "async exit", "outer exit", "async dispose", "outer dispose"];
        Assert.Equal(expected, lifetime.Events);
        Assert.True(lifetime.Probe!.Disposed);
    }

    [Fact]
    public async Task JsonResultsUseTheConfiguredJsonOptions()
    {
        await using var app = new TestApplication();
        Assert.Equal("{\"text\":\"typed\"}", (await app.SendAsync("/test/typed")).Text);
    }

    [Fact]
    public async Task CustomResultsExecuteInsideTheRequestScope()
    {
        await using var app    = new TestApplication();
        var             first  = await app.SendAsync("/test/custom-result");
        var             second = await app.SendAsync("/test/custom-result");
        Assert.Equal("custom result", first.Text);
        Assert.NotNull(first.Headers["X-Result-Probe"]);
        Assert.NotEqual(first.Headers["X-Result-Probe"], second.Headers["X-Result-Probe"]);
    }

    [Fact]
    public async Task NullResultsAreHandledAsApplicationErrors()
    {
        await using var app      = new TestApplication();
        var             response = await app.SendAsync("/test/null-result");
        Assert.Equal(StatusCodes.InternalServerError, response.StatusCode);
        Assert.DoesNotContain("IActionResult", response.Text);
    }

    [Fact]
    public async Task StatusAndRedirectResultsSetTheResponse()
    {
        await using var app = new TestApplication();
        Assert.Equal(StatusCodes.NoContent, (await app.SendAsync("/test/status-result")).StatusCode);
        var redirect = await app.SendAsync("/test/redirect-result");
        Assert.Equal(StatusCodes.SeeOther, redirect.StatusCode);
        Assert.Equal("/test/task", redirect.Headers[HeaderNames.Location]);
    }

    [Fact]
    public async Task HttpErrorsUseTheStandardShapeAndHideUnexpectedDetails()
    {
        await using var app      = new TestApplication();
        var             expected = await app.SendAsync("/test/error");
        Assert.Equal(StatusCodes.UnprocessableContent, expected.StatusCode);
        Assert.Equal(MimeTypes.ProblemJson, expected.Headers[HeaderNames.ContentType]);
        using var problem = JsonDocument.Parse(expected.Text);
        Assert.Equal("validation_failed", problem.RootElement.GetProperty("code").GetString());
        Assert.Equal("A safe error.", problem.RootElement.GetProperty("detail").GetString());
        Assert.NotEmpty(problem.RootElement.GetProperty("traceId").GetString()!);
        var unexpected = await app.SendAsync("/test/crash");
        Assert.Equal(StatusCodes.InternalServerError, unexpected.StatusCode);
        Assert.DoesNotContain("secret", unexpected.Text);
    }

    [Fact]
    public async Task ExceptionHandlersAreReplaceable()
    {
        await using var app      = new TestApplication(configureServices: services => services.AddSingleton<IRequestExceptionHandler, CustomHandler>());
        var             response = await app.SendAsync("/test/error");
        Assert.Equal(StatusCodes.Conflict, response.StatusCode);
        Assert.Equal("custom", response.Text);
    }

    [Fact]
    public async Task FailuresAfterCommitAbortInsteadOfWritingAnotherResponse()
    {
        await using var app      = new TestApplication();
        var             response = await app.SendAsync("/test/partial");
        Assert.True(response.Aborted);
        Assert.Equal("partial", response.Text);
    }

    [Fact]
    public async Task RequestTimeoutReturns408()
    {
        await using var app = new TestApplication(options => options.RequestTimeout = TimeSpan.FromMilliseconds(40));
        Assert.Equal(StatusCodes.RequestTimeout, (await app.SendAsync("/test/slow")).StatusCode);
    }

    [Theory]
    [InlineData("{\"Text\":\"hello\"}", MimeTypes.Json, 200)]
    [InlineData("{broken", MimeTypes.Json, 400)]
    [InlineData("null", MimeTypes.Json, 400)]
    [InlineData("{}", "text/plain", 415)]
    public async Task JsonHelpersValidateInput(string body, string contentType, int expected)
    {
        await using var app = new TestApplication();
        Assert.Equal(expected, (await app.SendAsync("/test/json", HttpMethods.Post, body, contentType)).StatusCode);
    }

    [Fact]
    public async Task BodyLimitsAreEnforcedWithoutContentLength()
    {
        await using var app = new TestApplication(options => options.MaxRequestBodyBytes = 8);
        Assert.Equal(StatusCodes.PayloadTooLarge, (await app.SendAsync("/test/json", HttpMethods.Post, "{\"Text\":\"hello\"}", MimeTypes.Json)).StatusCode);
    }

    [Fact]
    public async Task MiddlewareArgumentsAndRequestScopesAreUsed()
    {
        await using var app = new TestApplication();
        var             one = await app.SendAsync("/test/scoped");
        var             two = await app.SendAsync("/test/scoped");
        Assert.Equal(one.Text, one.Headers["X-Probe"]);
        Assert.Equal(one.Text, one.Headers["X-Attribute-Probe"]);
        Assert.Equal(two.Text, two.Headers["X-Attribute-Probe"]);
        Assert.Equal("42", one.Headers["X-Argument"]);
        Assert.NotEqual(one.Text, two.Text);
    }

    [Theory]
    [InlineData("?number=4", 200)]
    [InlineData("?number=bad", 400)]
    [InlineData("?number=1&number=2", 400)]
    public async Task QueryHelpersRejectMissingOrAmbiguousValues(string query, int expected)
    {
        await using var app = new TestApplication();
        Assert.Equal(expected, (await app.SendAsync("/test/query" + query)).StatusCode);
    }

    private sealed class CustomHandler : IRequestExceptionHandler
    {
        public ValueTask HandleAsync(RequestContext context, Exception exception)
        {
            context.Response.StatusCode = StatusCodes.Conflict;
            return context.Response.WriteTextAsync("custom");
        }
    }
}
