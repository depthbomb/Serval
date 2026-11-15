#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using Serval.Http;
using Serval.Http.Constants;
using Serval.Sample.Exceptions;
using Serval.Tests.Support;

namespace Serval.Tests.Samples;

public sealed class SampleRecipeTests
{
    [Theory]
    [InlineData(null, "/documents/1", "GET", 401)]
    [InlineData("reader-demo", "/documents/1", "GET", 200)]
    [InlineData("reader-demo", "/documents/2", "GET", 403)]
    [InlineData("reader-demo", "/documents/1/edit-preview", "POST", 403)]
    [InlineData("editor-demo", "/documents/2/edit-preview", "POST", 403)]
    [InlineData("editor-demo", "/documents/3/edit-preview", "POST", 200)]
    [InlineData("editor-demo", "/documents/999", "GET", 404)]
    public async Task TypedPermissionSampleChecksOwnershipAndHonorsTheLockVeto(string? token, string path, string method, int status)
    {
        await using var app      = new SampleApplication("BearerAuth");
        var             response = await app.SendAsync(path, method, configure: headers => headers[HeaderNames.Authorization] = token is null ? null : "Bearer " + token);
        Assert.Equal(status, response.StatusCode);
        if (status == 401)
        {
            Assert.Equal("Bearer", response.Headers[HeaderNames.WwwAuthenticate]);
        }
        else if (status == 403)
        {
            Assert.Equal("document_access_denied", Json(response).GetProperty("code").GetString());
            Assert.DoesNotContain("Approved documents", response.Text);
        }
    }

    [Fact]
    public async Task CentralExceptionSampleCanRenderHtmlAndEncodePublicMessages()
    {
        await using var app      = new SampleApplication("Exceptions");
        var             response = await app.SendAsync("/conflict?format=html");
        Assert.Equal(409, response.StatusCode);
        Assert.Equal(MimeTypes.Html, response.Headers[HeaderNames.ContentType]);
        Assert.Contains("<!doctype html>", response.Text);
        Assert.Contains("The item changed.", response.Text);

        await using var services = new SecurityTestApplication(_ => { });
        await using var request  = services.Request("https://app.test/error?format=html");
        var             handler  = new SampleExceptionHandler(NullLogger<SampleExceptionHandler>.Instance);
        await handler.HandleAsync(request.Context, new HttpErrorException(422, "<script>alert('test')</script>"));
        Assert.Contains("&lt;script&gt;", request.Response.Text);
        Assert.DoesNotContain("<script>", request.Response.Text);
    }

    [Fact]
    public async Task RoutingRecipesMatchConstraintsDefaultsQueriesAndNamedUrls()
    {
        await using var app = new SampleApplication("Routing");
        Assert.Equal("The literal route wins.", (await app.SendAsync("/products/featured")).Text);
        Assert.Equal(42, Json(await app.SendAsync("/products/42")).GetProperty("id").GetInt32());
        Assert.Equal(404, (await app.SendAsync("/products/0")).StatusCode);
        Assert.Equal(404, (await app.SendAsync("/articles/Uppercase")).StatusCode);
        Assert.Equal("hello-serval", (await app.SendAsync("/articles/hello-serval")).Text);
        Assert.Equal(2026, Json(await app.SendAsync("/archive")).GetProperty("year").GetInt32());
        Assert.Equal("Hello, visitor!", (await app.SendAsync("/hello")).Text);
        Assert.Equal("guides/first-steps", Json(await app.SendAsync("/tree/guides/first-steps")).GetProperty("path").GetString());

        var search = Json(await app.SendAsync("/search?page=2&tag=csharp&tag=http"));
        Assert.Equal(2, search.GetProperty("page").GetInt32());
        Assert.Equal(2, search.GetProperty("tags").GetArrayLength());
        Assert.Equal(400, (await app.SendAsync("/search?page=1&page=2")).StatusCode);
        var defaults = Json(await app.SendAsync("/search"));
        Assert.Equal(1, defaults.GetProperty("page").GetInt32());
        Assert.Empty(defaults.GetProperty("tags").EnumerateArray());
        Assert.Equal(400, (await app.SendAsync("/search?page=0")).StatusCode);
        Assert.Equal(400, (await app.SendAsync("/search?page=oops")).StatusCode);

        var links = Json(await app.SendAsync("/links"));
        Assert.Equal("/products/42?ref=samples%20%26%20recipes", links.GetProperty("product").GetString());
        Assert.Equal("/tree/guides/first%20steps", links.GetProperty("tree").GetString());
        Assert.Equal("/archive/2026", links.GetProperty("archive").GetString());
        Assert.Equal("/hello", links.GetProperty("hello").GetString());
        Assert.Equal("/hello/Reese%20%26%20friends", links.GetProperty("personal").GetString());
        Assert.Equal("Hello, Reese & friends!", (await app.SendAsync(links.GetProperty("personal").GetString()!)).Text);
        Assert.Equal("", Json(await app.SendAsync("/tree")).GetProperty("path").GetString());

        var redirect = await app.SendAsync("/products/42/?q=one");
        Assert.Equal(308, redirect.StatusCode);
        Assert.Equal("/products/42?q=one", redirect.Headers[HeaderNames.Location]);
        Assert.Contains("HEAD", (await app.SendAsync("/products/42", "OPTIONS")).Headers[HeaderNames.Allow]!);
    }

    [Fact]
    public async Task RoutingSampleBindsHeadersAndActionServices()
    {
        await using var app      = new SampleApplication("Routing");
        var             defaults = Json(await app.SendAsync("/request-info"));
        Assert.Equal(JsonValueKind.Null, defaults.GetProperty("requestId").ValueKind);
        Assert.Equal("browser", defaults.GetProperty("client").GetString());
        Assert.True(defaults.GetProperty("utc").TryGetDateTimeOffset(out _));

        var requestId = Guid.NewGuid();
        var response = await app.SendAsync("/request-info", configure: headers =>
        {
            headers["X-Request-Id"] = requestId.ToString();
            headers["X-Client"]     = "routing-demo";
        });
        Assert.Equal(200, response.StatusCode);
        Assert.Equal(requestId, Json(response).GetProperty("requestId").GetGuid());
        Assert.Equal("routing-demo", Json(response).GetProperty("client").GetString());
        Assert.Equal(400, (await app.SendAsync("/request-info", configure: headers => headers["X-Request-Id"] = "invalid")).StatusCode);
    }

    [Fact]
    public async Task TodoRecipeSupportsCrudPaginationAndValidation()
    {
        await using var app     = new SampleApplication("TodoApi");
        var             created = await app.SendAsync("/todos", "POST", "{\"title\":\"  Try Serval  \"}");
        Assert.Equal(201, created.StatusCode);
        var location = created.Headers[HeaderNames.Location]!;
        Assert.Equal("Try Serval", Json(await app.SendAsync(location)).GetProperty("title").GetString());
        Assert.Equal(200, (await app.SendAsync(location, "PUT", "{\"title\":\"Done\",\"completed\":true}")).StatusCode);
        Assert.Equal(1, Json(await app.SendAsync("/todos?completed=true&offset=0&limit=1")).GetArrayLength());
        Assert.Equal(0, Json(await app.SendAsync("/todos?completed=false")).GetArrayLength());
        Assert.Equal(400, (await app.SendAsync("/todos?limit=101")).StatusCode);
        Assert.Equal(422, (await app.SendAsync("/todos", "POST", "{}")).StatusCode);
        Assert.Equal(400, (await app.SendAsync("/todos", "POST", "{broken")).StatusCode);
        Assert.Equal(415, (await app.SendAsync("/todos", "POST", "{}", headers => headers[HeaderNames.ContentType] = MimeTypes.Text)).StatusCode);
        Assert.Equal(413, (await app.SendAsync("/todos", "POST", new string('x', 16 * 1024 + 1))).StatusCode);
        Assert.Equal(204, (await app.SendAsync(location, "DELETE")).StatusCode);
        Assert.Equal(404, (await app.SendAsync(location)).StatusCode);
        Assert.Equal(404, (await app.SendAsync(location, "PUT", "{\"title\":\"Gone\"}")).StatusCode);
    }

    [Fact]
    public async Task TodoStoreIsSharedAcrossRequestScopesAndHandlesConcurrentWrites()
    {
        await using var app       = new SampleApplication("TodoApi");
        var             responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(index => app.SendAsync("/todos", "POST", $"{{\"title\":\"Task {index}\"}}")));
        Assert.All(responses, response => Assert.Equal(201, response.StatusCode));
        Assert.Equal(20, responses.Select(response => Json(response).GetProperty("id").GetInt32()).Distinct().Count());
        Assert.Equal(20, Json(await app.SendAsync("/todos")).GetArrayLength());
    }

    [Fact]
    public async Task FileRecipesSupportConditionalRequestsAttachmentsAndBoundedBodies()
    {
        await using var app  = new SampleApplication("Files");
        var             file = await app.SendAsync("/sample-data.json");
        Assert.Equal(200, file.StatusCode);
        Assert.Contains("Serval", file.Text);
        var cached = await app.SendAsync("/sample-data.json", configure: headers => headers[HeaderNames.IfNoneMatch] = file.Headers[HeaderNames.ETag]);
        Assert.Equal(304, cached.StatusCode);
        Assert.Empty(cached.Text);
        var head = await app.SendAsync("/sample-data.json", "HEAD");
        Assert.Equal(200, head.StatusCode);
        Assert.Empty(head.Text);
        Assert.Equal(file.Headers[HeaderNames.ContentLength], head.Headers[HeaderNames.ContentLength]);
        var unmodified = await app.SendAsync("/sample-data.json", configure: headers => headers[HeaderNames.IfModifiedSince] = file.Headers[HeaderNames.LastModified]);
        Assert.Equal(304, unmodified.StatusCode);
        var download = await app.SendAsync("/downloads/report");
        Assert.Contains("report.csv", download.Headers[HeaderNames.ContentDisposition]!);
        Assert.Equal(404, (await app.SendAsync("/downloads/unknown")).StatusCode);
        Assert.Equal(404, (await app.SendAsync("/Downloads/report.csv")).StatusCode);
        var csv = await app.SendAsync("/exports/report");
        Assert.Contains("\"HTTP, routing, and middleware\"", csv.Text);
        Assert.Contains("\"\"value\"\"", csv.Text);
        var digest = Json(await app.SendAsync("/uploads/digest", "POST", "abc"));
        Assert.Equal(3, digest.GetProperty("length").GetInt32());
        Assert.Equal("BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD", digest.GetProperty("sha256").GetString());
        Assert.Equal(413, (await app.SendAsync("/uploads/digest", "POST", new string('x', 64 * 1024 + 1))).StatusCode);
    }

    [Fact]
    public async Task CookieRecipeRequiresCredentialsAndCsrfThenRevokesTheSession()
    {
        await using var app         = new SampleApplication("CookieAuth");
        const string    credentials = "{\"login\":\"demo\",\"password\":\"serval-sample-password\"}";
        Assert.Equal(401, (await app.SendAsync("/account/me")).StatusCode);
        Assert.Equal(400, (await app.SendAsync("/account/login", "POST", credentials)).StatusCode);
        Assert.Equal(401, (await app.SendAsync("/account/login", "POST", "{\"login\":\"demo\",\"password\":\"wrong\"}", headers => headers["X-Serval-Demo"] = "login")).StatusCode);
        var login = await app.SendAsync("/account/login", "POST", credentials, headers => headers["X-Serval-Demo"] = "login");
        Assert.Equal(200, login.StatusCode);
        var setCookie = Assert.Single(login.Headers.GetValues(HeaderNames.SetCookie));
        Assert.Contains("HttpOnly", setCookie);
        Assert.Contains("SameSite=Strict", setCookie);
        var cookie  = setCookie.Split(';')[0];
        var profile = await app.SendAsync("/account/me", configure: headers => headers[HeaderNames.Cookie] = cookie);
        Assert.Equal("demo", Json(profile).GetProperty("name").GetString());
        var csrf  = await app.SendAsync("/account/csrf", configure: headers => headers[HeaderNames.Cookie] = cookie);
        var token = Json(csrf).GetProperty("token").GetString();
        Assert.Equal(403, (await app.SendAsync("/account/logout", "POST", configure: headers => headers[HeaderNames.Cookie] = cookie)).StatusCode);
        var logout = await app.SendAsync("/account/logout", "POST", configure: headers =>
        {
            headers[HeaderNames.Cookie] = cookie;
            headers["X-CSRF-Token"]     = token;
        });
        Assert.Equal(204, logout.StatusCode);
        Assert.Equal(401, (await app.SendAsync("/account/me", configure: headers => headers[HeaderNames.Cookie] = cookie)).StatusCode);
    }

    [Theory]
    [InlineData(null, "/reports", "GET", 401)]
    [InlineData("wrong", "/reports", "GET", 401)]
    [InlineData("reader-demo", "/reports", "GET", 200)]
    [InlineData("reader-demo", "/reports/preview", "POST", 403)]
    [InlineData("editor-demo", "/reports/preview", "POST", 200)]
    [InlineData("reader-demo", "/reports/bob", "GET", 200)]
    [InlineData("reader-demo", "/reports/alice", "GET", 403)]
    [InlineData("editor-demo", "/reports/alice", "GET", 200)]
    public async Task BearerRecipeDistinguishesAuthenticationRolesClaimsAndOwnership(string? token, string path, string method, int status)
    {
        await using var app      = new SampleApplication("BearerAuth");
        var             response = await app.SendAsync(path, method, configure: headers => headers[HeaderNames.Authorization] = token is null ? null : "Bearer " + token);
        Assert.Equal(status, response.StatusCode);
        if (status == 401)
        {
            Assert.Equal("Bearer", response.Headers[HeaderNames.WwwAuthenticate]);
        }
    }

    [Fact]
    public async Task StreamingSampleServesItsBrowserClient()
    {
        await using var app  = new SampleApplication("Streaming");
        var             page = await app.SendAsync("/");
        Assert.Equal(200, page.StatusCode);
        Assert.Equal(MimeTypes.Html, page.Headers[HeaderNames.ContentType]);
        Assert.Contains("/streaming/events.js", page.Text);
        Assert.Contains("/streaming/socket.js", page.Text);

        foreach (var asset in new[] { "events.js", "socket.js", "log.js", "site.css" })
        {
            var response = await app.SendAsync("/streaming/" + asset);
            Assert.Equal(200, response.StatusCode);
            Assert.NotEmpty(response.Text);
            Assert.StartsWith(asset.EndsWith(".js", StringComparison.Ordinal) ? "text/javascript" : "text/css",
                response.Headers[HeaderNames.ContentType]);
        }
    }

    [Theory]
    [InlineData("batch=0")]
    [InlineData("batch=11")]
    [InlineData("batch=invalid")]
    [InlineData("batch=3&batch=4")]
    public async Task StreamingSampleRejectsInvalidBatchSizes(string query)
    {
        await using var app      = new SampleApplication("Streaming");
        var             response = await app.SendAsync("/events?" + query);
        Assert.Equal(400, response.StatusCode);
        Assert.DoesNotContain("event: tick", response.Text);
    }

    [Fact]
    public async Task StreamingRecipeResumesEventsAndStopsCompletedSequences()
    {
        await using var app      = new SampleApplication("Streaming");
        var             response = await app.SendAsync("/events", configure: headers => headers["Last-Event-ID"] = "8");
        Assert.Equal(200, response.StatusCode);
        Assert.Contains(": heartbeat", response.Text);
        Assert.Contains("id: 9\n", response.Text);
        Assert.Contains("id: 10\n", response.Text);
        Assert.DoesNotContain("id: 8\n", response.Text);
        Assert.Equal(204, (await app.SendAsync("/events", configure: headers => headers["Last-Event-ID"] = "10")).StatusCode);
        Assert.Equal(400, (await app.SendAsync("/events", configure: headers => headers["Last-Event-ID"] = "invalid")).StatusCode);
        var records = await app.SendAsync("/records");
        var lines   = records.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(5, lines.Length);
        using var last = JsonDocument.Parse(lines[^1]);
        Assert.Equal(5, last.RootElement.GetProperty("index").GetInt32());
    }

    [Fact]
    public async Task OAuthSampleExplainsMissingConfigurationWithoutCallingAProvider()
    {
        await using var app = new SampleApplication("OAuth");
        Assert.Contains("Provider configured: False", (await app.SendAsync("/")).Text);
        Assert.Equal(503, (await app.SendAsync("/auth/login")).StatusCode);
        Assert.Equal(401, (await app.SendAsync("/auth/me")).StatusCode);
    }

    [Theory]
    [InlineData("/crash", 500, "unexpected_error")]
    [InlineData("/middleware", 500, "unexpected_error")]
    [InlineData("/result", 500, "unexpected_error")]
    [InlineData("/conflict", 409, "inventory_conflict")]
    [InlineData("/limited", 429, "rate_limited")]
    [InlineData("/slow", 408, "http_error")]
    [InlineData("/missing", 404, "http_error")]
    public async Task CentralExceptionRecipeHandlesFailuresWithSafeCorrelatedResponses(string path, int status, string code)
    {
        await using var app      = new SampleApplication("Exceptions");
        var             response = await app.SendAsync(path);
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("no-store", response.Headers[HeaderNames.CacheControl]);
        var json = Json(response);
        Assert.Equal(code, json.GetProperty("code").GetString());
        Assert.Equal(path, json.GetProperty("path").GetString());
        Assert.Equal(response.Headers["X-Trace-Id"], json.GetProperty("traceId").GetString());
        Assert.DoesNotContain("server logs only", response.Text);
        Assert.DoesNotContain("InvalidOperationException", response.Text);
        if (status == 429)
        {
            Assert.Equal("30", response.Headers["Retry-After"]);
        }
    }

    [Fact]
    public async Task CentralExceptionRecipeAbortsCommittedResponsesWithoutAppendingAnError()
    {
        await using var app      = new SampleApplication("Exceptions");
        var             response = await app.SendAsync("/partial");
        Assert.True(response.Aborted);
        Assert.Equal("The response has already started.\n", response.Text);
        Assert.Null(response.Headers["X-Trace-Id"]);
    }

    private static JsonElement Json(MemoryResponse response)
    {
        using var document = JsonDocument.Parse(response.Text);
        return document.RootElement.Clone();
    }
}
