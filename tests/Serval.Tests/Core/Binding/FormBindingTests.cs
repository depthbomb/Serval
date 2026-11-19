#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Serval.Binding;
using Serval.Http;
using Serval.Http.Constants;
using Serval.Http.Results;
using Serval.Routing;
using Serval.Tests.Support;

namespace Serval.Tests.Core.Binding;

public sealed class FormBindingTests
{
    [Theory]
    [InlineData("count=4&tags=1&tags=2", 200, "4:3:missing:Friday")]
    [InlineData("count=4&count=5", 400, null)]
    [InlineData("count=invalid", 400, null)]
    [InlineData("tags=1", 400, null)]
    [InlineData("count=4&tags=invalid", 400, null)]
    public async Task AttributesBindFormScalarsArraysDefaultsAndEnums(string body, int status, string? expected)
    {
        await using var app      = new TestApplication();
        var             response = await app.SendAsync("/form-bindings/fields", "POST", body, MimeTypes.FormUrlEncoded);
        Assert.Equal(status, response.StatusCode);
        if (expected is not null)
        {
            Assert.Equal(expected, response.Text);
        }
    }

    [Fact]
    public async Task FilesBindAndFormParsingRunsInsideMiddleware()
    {
        await using var app      = new TestApplication();
        const string    body     = "--example\r\nContent-Disposition: form-data; name=\"file\"; filename=\"a.txt\"\r\n\r\nhello\r\n--example--\r\n";
        var             response = await app.SendAsync("/form-bindings/file", "POST", body, "multipart/form-data; boundary=example");
        Assert.Equal(200, response.StatusCode);
        Assert.Equal("a.txt:5:1:missing", response.Text);
        Assert.Equal(400, (await app.SendAsync("/form-bindings/file", "POST", "", MimeTypes.FormUrlEncoded)).StatusCode);
        Assert.Equal(415, (await app.SendAsync("/form-bindings/fields", "POST", "{\"count\":1}", MimeTypes.Json)).StatusCode);
        Assert.Equal(403, (await app.SendAsync("/form-bindings/fields", "POST", "invalid", "text/plain", headers => headers["X-Reject"] = "yes")).StatusCode);
    }

    [Theory]
    [InlineData("application/x-www-form-urlencoded", "username=reese&ids=1&ids=2")]
    [InlineData("application/json", "{\"username\":\"reese\",\"ids\":[1,2]}")]
    public async Task ControllerHelpersReadAllSourcesWithoutAttributes(string type, string body)
    {
        await using var app      = new TestApplication();
        var             response = await app.SendAsync("/form-bindings/helpers/12?tags=2&tags=3&day=friday", "POST", body, type);
        Assert.Equal(200, response.StatusCode);
        Assert.Equal("12:12:7:9:True:False:Friday:reese:3:5", response.Text);
        Assert.Equal(400, (await app.SendAsync("/form-bindings/helpers/12?limit=invalid", "POST", body, type)).StatusCode);
        Assert.Equal(400, (await app.SendAsync("/form-bindings/helpers/12?limit=1&limit=2", "POST", body, type)).StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PipelineDisposesUploadStorageAfterSuccessOrActionFailure(bool fail)
    {
        var directory = Path.Combine(Path.GetTempPath(), "serval-form-pipeline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        UploadedFile? captured = null;
        await using var app = new TestApplication(options =>
        {
            options.Forms.TempDirectory   = directory;
            options.Forms.MemoryThreshold = 1;
        }, configureBuilder: builder => builder.AddController<object>("POST", "/capture-form", null, async (_, context) =>
        {
            captured = (await context.Request.ReadFormAsync()).GetFile("file");
            Assert.Single(Directory.GetFiles(directory));
            return fail ? throw new HttpErrorException(422) : new TextResult("ok");
        }));
        const string body = "--example\r\nContent-Disposition: form-data; name=\"file\"; filename=\"a.txt\"\r\n\r\nhello\r\n--example--\r\n";
        Assert.Equal(fail ? 422 : 200, (await app.SendAsync("/capture-form", "POST", body, "multipart/form-data; boundary=example")).StatusCode);
        Assert.Empty(Directory.GetFiles(directory));
        Assert.NotNull(captured);
        Assert.Throws<ObjectDisposedException>(() => captured.OpenReadStream());
    }
}

[Controller]
[Route("form-bindings")]
public sealed class FormBindingController : ControllerBase
{
    [Post("fields")]
    [RejectBody]
    public ValueTask<IActionResult> Fields([FromForm] int count, [FromForm] int[] tags, [FromForm] string? label, [FromForm] DayOfWeek day = DayOfWeek.Friday)
        => TextAsync($"{count}:{tags.Sum()}:{label ?? "missing"}:{day}");

    [Post("file")]
    public ValueTask<IActionResult> File([FromForm] UploadedFile file, [FromForm("file")] UploadedFile[] copies, [FromForm] UploadedFile? optional)
        => TextAsync($"{file.FileName}:{file.Length}:{copies.Length}:{optional?.FileName ?? "missing"}");

    [Post("helpers/{id:int}")]
    public async ValueTask<IActionResult> Helpers()
    {
        var route    = RouteValue<int>("id");
        var raw      = RouteValue("id");
        var fallback = RouteValue("absent", 7);
        var limit    = QueryValue("limit", 9);
        var present  = TryRouteValue<int>("id", out _);
        var missing  = TryQueryValue<int>("missing", out _);
        var day      = Request.Query.ContainsKey("day") ? QueryEnum<DayOfWeek>("day") : DayOfWeek.Friday;
        var post     = await ReadPostAsync();
        var username = await PostValueAsync<string>("username");
        var ids      = post.GetValues<int>("ids");
        return Text($"{route}:{raw}:{fallback}:{limit}:{present}:{missing}:{day}:{username}:{ids.Sum()}:{QueryValues<int>("tags").Sum()}");
    }
}
