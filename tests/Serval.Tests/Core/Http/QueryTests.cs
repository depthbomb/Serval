#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Serval.Http.Constants;
using Serval.Http.Results;
using Serval.Tests.Support;

namespace Serval.Tests.Core.Http;

public sealed class QueryTests
{
    [Theory]
    [InlineData("/query/search")]
    [InlineData("/query/custom")]
    [InlineData("/query/programmatic")]
    public async Task GeneratedAndProgrammaticRoutesReadQueryBodies(string path)
    {
        await using var app = new TestApplication(configureBuilder: builder => builder.AddController<object>(HttpMethods.Query, "/query/programmatic", null, async (_, context) =>
        {
            var query = await context.Request.ReadJsonAsync(TestJsonContext.Default.Message, context.RequestAborted);
            return new TextResult(query.Text);
        }));
        var response = await app.SendAsync(path, HttpMethods.Query, "{\"Text\":\"find café\"}", MimeTypes.Json);
        Assert.Equal(StatusCodes.Ok, response.StatusCode);
        Assert.Equal("find café", response.Text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("invalid")]
    [InlineData("application/json, text/plain")]
    [InlineData("application/json; charset=utf-8; CHARSET=utf-8")]
    public async Task MissingOrMalformedContentTypeFailsBeforeApplicationMiddleware(string? contentType)
    {
        var invoked = false;
        await using var app = new TestApplication(configureBuilder: builder => builder.Use(async (context, next) =>
        {
            invoked = true;
            await next(context);
        }));
        var response = await app.SendAsync("/query/raw", HttpMethods.Query, "{}", contentType);
        Assert.Equal(StatusCodes.BadRequest, response.StatusCode);
        Assert.False(invoked);
    }

    [Fact]
    public async Task RepeatedContentTypeFieldsAreRejected()
    {
        await using var app = new TestApplication();
        var response = await app.SendAsync("/query/raw", HttpMethods.Query, "{}", MimeTypes.Json,
            headers => headers.Append(HeaderNames.ContentType, MimeTypes.Json));
        Assert.Equal(StatusCodes.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("{", MimeTypes.Json, StatusCodes.BadRequest)]
    [InlineData("null", MimeTypes.Json, StatusCodes.BadRequest)]
    [InlineData("{}", MimeTypes.PlainText, StatusCodes.UnsupportedMediaType)]
    public async Task QueryBindingRejectsInvalidContentAndUnsupportedFormats(string body, string contentType, int status)
    {
        await using var app      = new TestApplication();
        var             response = await app.SendAsync("/query/search", HttpMethods.Query, body, contentType);
        Assert.Equal(status, response.StatusCode);
    }

    [Fact]
    public async Task FormQueriesUseExistingBodyBinding()
    {
        await using var app      = new TestApplication();
        var             response = await app.SendAsync("/query/form", HttpMethods.Query, "term=caf%C3%A9+books", MimeTypes.FormUrlEncoded);
        Assert.Equal(StatusCodes.Ok, response.StatusCode);
        Assert.Equal("café books", response.Text);
    }

    [Fact]
    public async Task QueryBodyLimitsAreEnforced()
    {
        await using var app      = new TestApplication(options => options.MaxRequestBodyBytes = 64);
        var             response = await app.SendAsync("/query/search", HttpMethods.Query, "{\"Text\":\"" + new string('x', 100) + "\"}", MimeTypes.Json);
        Assert.Equal(StatusCodes.PayloadTooLarge, response.StatusCode);
    }

    [Theory]
    [InlineData(HttpMethods.Options, StatusCodes.NoContent)]
    [InlineData(HttpMethods.Get, StatusCodes.MethodNotAllowed)]
    [InlineData(HttpMethods.Head, StatusCodes.MethodNotAllowed)]
    [InlineData(HttpMethods.Post, StatusCodes.MethodNotAllowed)]
    public async Task QueryRoutesAdvertiseTheirMethodWithoutAHeadFallback(string method, int status)
    {
        await using var app      = new TestApplication();
        var             response = await app.SendAsync("/query/search", method);
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("OPTIONS, QUERY", response.Headers[HeaderNames.Allow]);
    }
}
