#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Text.Json;
using Serval.Http.Constants;
using Serval.Tests.Core.Http;
using Serval.Tests.Support;

namespace Serval.Tests.Samples;

public sealed class FormSampleTests
{
    [Theory]
    [InlineData("application/x-www-form-urlencoded", "username=reese&ids=1&ids=2")]
    [InlineData("application/json", "{\"username\":\"reese\",\"ids\":[1,2]}")]
    public async Task FormAndJsonRequestsUseTheSameSampleHandler(string type, string body)
    {
        await using var app      = new SampleApplication("Forms");
        var             response = await app.SendAsync("/submit/42?limit=5", "POST", body, headers => headers[HeaderNames.ContentType] = type);
        Assert.Equal(200, response.StatusCode);
        using var json = JsonDocument.Parse(response.Text);
        Assert.Equal(42, json.RootElement.GetProperty("id").GetInt32());
        Assert.Equal(5, json.RootElement.GetProperty("limit").GetInt32());
        Assert.Equal("reese", json.RootElement.GetProperty("username").GetString());
        Assert.Equal(2, json.RootElement.GetProperty("ids").GetArrayLength());
    }

    [Fact]
    public async Task BrowserPageAndMultipartFileSampleWork()
    {
        await using var app  = new SampleApplication("Forms");
        var             page = await app.SendAsync("/");
        Assert.Equal("text/html; charset=utf-8", page.Headers[HeaderNames.ContentType]);
        Assert.Contains("multipart/form-data", page.Text);
        var body     = FormDataTests.Multipart(("username", "reese")).Replace("--example--\r\n", "--example\r\nContent-Disposition: form-data; name=\"files\"; filename=\"a.txt\"\r\n\r\nabc\r\n--example--\r\n");
        var response = await app.SendAsync("/submit/42", "POST", body, headers => headers[HeaderNames.ContentType] = "multipart/form-data; boundary=example");
        Assert.Equal(200, response.StatusCode);
        using var json = JsonDocument.Parse(response.Text);
        var       file = json.RootElement.GetProperty("files")[0];
        Assert.Equal(3, file.GetProperty("length").GetInt64());
        Assert.Equal("BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD", file.GetProperty("sha256").GetString());
    }
}
