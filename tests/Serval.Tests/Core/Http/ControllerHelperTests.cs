#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Serialization.Metadata;
using Serval.Http;
using Serval.Http.Constants;
using Serval.Routing;
using Serval.Tests.Support;

namespace Serval.Tests.Core.Http;

public sealed class ControllerHelperTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AbortHelpersRespectTheirConditions(bool condition)
    {
        await using var app = new TestApplication();

        var response = new MemoryResponse();
        var context  = CreateContext(app, response);
        var error    = Assert.Throws<HttpErrorException>(() => context.Abort(StatusCodes.Forbidden, "Denied."));
        Assert.Equal(StatusCodes.Forbidden, error.StatusCode);
        Assert.Equal("Denied.", error.Message);
        if (condition)
        {
            Assert.Throws<HttpErrorException>(() => context.AbortIf(condition, StatusCodes.Forbidden));
            context.AbortUnless(condition, StatusCodes.Forbidden);
        }
        else
        {
            context.AbortIf(condition, StatusCodes.Forbidden);
            Assert.Throws<HttpErrorException>(() => context.AbortUnless(condition, StatusCodes.Forbidden));
        }

        Assert.False(response.HasStarted);
    }

    [Fact]
    public async Task TextAndJsonHelpersUseTheCurrentRequest()
    {
        await using var app = new TestApplication();

        var textResponse = new MemoryResponse();
        var text         = CreateContext(app, textResponse);
        var textResult   = await text.TextAsync("hello");
        Assert.False(textResponse.HasStarted);
        await textResult.ExecuteAsync(text.Context);
        Assert.Equal("hello", textResponse.Text);
        Assert.Equal(MimeTypes.Text, textResponse.Headers[HeaderNames.ContentType]);

        var jsonResponse = new MemoryResponse();
        var json         = CreateContext(app, jsonResponse);
        var jsonResult   = await json.JsonAsync(new Message("hello"), TestJsonContext.Default.Message);
        Assert.False(jsonResponse.HasStarted);
        await jsonResult.ExecuteAsync(json.Context);
        Assert.Equal("{\"Text\":\"hello\"}", jsonResponse.Text);
        Assert.Equal(MimeTypes.Json, jsonResponse.Headers[HeaderNames.ContentType]);
    }

    [Fact]
    public async Task FileHelpersStreamFilesAndEncodeDownloadNames()
    {
        var path  = Path.Combine(Path.GetTempPath(), "serval-file-" + Guid.NewGuid().ToString("N"));
        var bytes = new byte[200_000];
        Random.Shared.NextBytes(bytes);
        await File.WriteAllBytesAsync(path, bytes);
        await using var app = new TestApplication();

        var response = new MemoryResponse();
        var context  = CreateContext(app, response);
        var result   = await context.FileAsync(path, downloadName: "résumé.bin");
        Assert.False(response.HasStarted);
        await result.ExecuteAsync(context.Context);
        Assert.Equal(bytes, response.Body.ToArray());
        Assert.Equal(bytes.Length.ToString(), response.Headers[HeaderNames.ContentLength]);
        Assert.Equal("résumé.bin", ContentDispositionHeaderValue.Parse(response.Headers[HeaderNames.ContentDisposition]!).FileNameStar);
    }

    [Fact]
    public async Task MissingFilesReturn404BeforeCommittingTheResponse()
    {
        var response  = new MemoryResponse();
        var exception = await Assert.ThrowsAsync<HttpErrorException>(() => response.WriteFileAsync(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))).AsTask());
        Assert.Equal(StatusCodes.NotFound, exception.StatusCode);
        Assert.False(response.HasStarted);
    }

    private static HelperController CreateContext(TestApplication app, MemoryResponse response)
    {
        var request    = new Request(HttpMethods.Get, new Uri("http://localhost/"), HttpVersion.Version11, new Headers(), Stream.Null, 1024);
        var controller = new HelperController();
        controller.AttachContext(new RequestContext(request, response, app.Services, app.Services.GetRequiredService<IUrlGenerator>(), CancellationToken.None));
        return controller;
    }

    private sealed class HelperController : ControllerBase
    {
        public new void Abort(int statusCode, string? message = null) => base.Abort(statusCode, message);

        public new void AbortIf(bool condition, int statusCode, string? message = null) => base.AbortIf(condition, statusCode, message);

        public new void AbortUnless(bool condition, int statusCode, string? message = null) => base.AbortUnless(condition, statusCode, message);

        public new ValueTask<IActionResult> TextAsync(string text, int? statusCode = null) => base.TextAsync(text, statusCode);

        public new ValueTask<IActionResult> JsonAsync<T>(T value, JsonTypeInfo<T>? typeInfo = null, int? statusCode = null) => base.JsonAsync(value, typeInfo, statusCode);

        public new ValueTask<IActionResult> FileAsync(string path, string? contentType = null, string? downloadName = null) => base.FileAsync(path, contentType, downloadName);
    }
}
