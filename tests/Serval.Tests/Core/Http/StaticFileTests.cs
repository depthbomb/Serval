#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Serval.Http.Constants;
using Serval.Http.StaticFiles;
using Serval.Tests.Support;

namespace Serval.Tests.Core.Http;

public sealed class StaticFileTests
{
    [Theory]
    [InlineData("font.OTF", "font/otf")]
    [InlineData("song.m4a", "audio/mp4")]
    [InlineData("clip.ogv", "video/ogg")]
    [InlineData("readme.md", "text/markdown; charset=utf-8")]
    [InlineData("calendar.ics", "text/calendar")]
    [InlineData("report.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document")]
    [InlineData("archive.gz", "application/gzip")]
    public async Task CommonPublicAssetsUseTheirMediaTypeWithoutChangingTheirBytes(string name, string expectedType)
    {
        var    root  = CreateRoot();
        byte[] bytes = [0, 1, 2, 127, 128, 255];
        await File.WriteAllBytesAsync(Path.Combine(root, name), bytes);
        await using var app = new TestApplication(options => options.StaticFiles.DirectoryPath = root);

        var response = await app.SendAsync("/" + name);
        Assert.Equal(StatusCodes.Ok, response.StatusCode);
        Assert.Equal(expectedType, response.Headers[HeaderNames.ContentType]);
        Assert.Equal(bytes.Length.ToString(), response.Headers[HeaderNames.ContentLength]);
        Assert.Equal(bytes, response.Body.ToArray());
        Assert.Null(response.Headers[HeaderNames.ContentEncoding]);
    }

    [Theory]
    [InlineData(StaticFilePriority.BeforeRouting, "public file")]
    [InlineData(StaticFilePriority.AfterRouting, "task")]
    public async Task StaticFilePriorityControlsCollisionsAndTheIndexFallbackRunsLast(StaticFilePriority priority, string expected)
    {
        var root = CreateRoot();
        Directory.CreateDirectory(Path.Combine(root, "test"));
        await File.WriteAllTextAsync(Path.Combine(root, "test", "task"), "public file");
        await File.WriteAllTextAsync(Path.Combine(root, "hello.txt"), "hello");
        await File.WriteAllTextAsync(Path.Combine(root, "index.html"), "app shell");
        await using var app = new TestApplication(options =>
        {
            options.StaticFiles.DirectoryPath         = root;
            options.StaticFiles.Priority              = priority;
            options.StaticFiles.ServeUnknownFileTypes = true;
            options.StaticFiles.FallbackToIndexHtml   = true;
        });
        Assert.Equal(expected, (await app.SendAsync("/test/task")).Text);
        Assert.Equal("new", (await app.SendAsync("/test/items/new")).Text);
        Assert.Equal("hello", (await app.SendAsync("/hello.txt")).Text);
        foreach (var path in new[] { "/", "/settings/profile?tab=account", "/settings/", "/missing.js" })
        {
            var response = await app.SendAsync(path);
            Assert.Equal(StatusCodes.Ok, response.StatusCode);
            Assert.Equal("app shell", response.Text);
            Assert.Equal(MimeTypes.Html, response.Headers[HeaderNames.ContentType]);
            Assert.Null(response.Headers[HeaderNames.Location]);
        }
    }

    [Fact]
    public async Task AController404DoesNotFallBackToAFileOrIndex()
    {
        var root = CreateRoot();
        Directory.CreateDirectory(Path.Combine(root, "test"));
        await File.WriteAllTextAsync(Path.Combine(root, "test", "not-found.txt"), "must not be served");
        await File.WriteAllTextAsync(Path.Combine(root, "index.html"), "app shell");
        await using var app = new TestApplication(options =>
        {
            options.StaticFiles.DirectoryPath       = root;
            options.StaticFiles.FallbackToIndexHtml = true;
        });
        var response = await app.SendAsync("/test/not-found.txt");
        Assert.Equal(StatusCodes.NotFound, response.StatusCode);
        Assert.Contains("Controller-owned 404.", response.Text);
        Assert.DoesNotContain("must not be served", response.Text);
        Assert.DoesNotContain("app shell", response.Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FilesAndIndexFallbackSupportHeadContentTypesAndConditionalRequests(bool fallback)
    {
        var root = CreateRoot();
        var path = fallback ? "/settings/profile" : "/hello.txt";
        await File.WriteAllTextAsync(Path.Combine(root, fallback ? "index.html" : "hello.txt"), "hello");
        await using var app = new TestApplication(options =>
        {
            options.StaticFiles.DirectoryPath       = root;
            options.StaticFiles.FallbackToIndexHtml = fallback;
            options.StaticFiles.CacheControl        = "private, max-age=0, must-revalidate";
        });
        var response = await app.SendAsync(path);
        Assert.Equal("hello", response.Text);
        Assert.Equal(fallback ? MimeTypes.Html : MimeTypes.Text, response.Headers[HeaderNames.ContentType]);
        Assert.Equal("5", response.Headers[HeaderNames.ContentLength]);
        Assert.Equal("private, max-age=0, must-revalidate", response.Headers[HeaderNames.CacheControl]);
        Assert.Equal("nosniff", response.Headers[HeaderNames.XContentTypeOptions]);
        var head = await app.SendAsync(path, HttpMethods.Head);
        Assert.Equal(StatusCodes.Ok, head.StatusCode);
        Assert.Empty(head.Text);
        Assert.Equal("5", head.Headers[HeaderNames.ContentLength]);
        Assert.Equal(response.Headers[HeaderNames.ETag], head.Headers[HeaderNames.ETag]);
        var cached = await app.SendAsync(path, configureHeaders: headers => headers[HeaderNames.IfNoneMatch] = response.Headers[HeaderNames.ETag]);
        Assert.Equal(StatusCodes.NotModified, cached.StatusCode);
        Assert.Empty(cached.Text);
        var dated = await app.SendAsync(path, configureHeaders: headers => headers[HeaderNames.IfModifiedSince] = response.Headers[HeaderNames.LastModified]);
        Assert.Equal(StatusCodes.NotModified, dated.StatusCode);
        var precedence = await app.SendAsync(path, configureHeaders: headers =>
        {
            headers[HeaderNames.IfNoneMatch]     = "\"different\"";
            headers[HeaderNames.IfModifiedSince] = response.Headers[HeaderNames.LastModified];
        });
        Assert.Equal(StatusCodes.Ok, precedence.StatusCode);
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public async Task IndexFallbackNeedsOptInAStaticDirectoryAndAnIndexFile(bool enabled, bool hasDirectory, bool hasIndex)
    {
        var root = CreateRoot();
        if (hasIndex)
        {
            await File.WriteAllTextAsync(Path.Combine(root, "index.html"), "app shell");
        }

        await using var app = new TestApplication(options =>
        {
            options.StaticFiles.DirectoryPath = hasDirectory ? root : null;
            if (enabled)
            {
                options.StaticFiles.FallbackToIndexHtml = true;
            }
        });
        Assert.Equal(StatusCodes.NotFound, (await app.SendAsync("/settings/profile")).StatusCode);
    }

    [Theory]
    [InlineData("/settings", HttpMethods.Post, StatusCodes.NotFound)]
    [InlineData("/settings", HttpMethods.Put, StatusCodes.NotFound)]
    [InlineData("/settings", HttpMethods.Patch, StatusCodes.NotFound)]
    [InlineData("/settings", HttpMethods.Delete, StatusCodes.NotFound)]
    [InlineData("/settings", HttpMethods.Options, StatusCodes.NotFound)]
    [InlineData("/test/json", HttpMethods.Get, StatusCodes.MethodNotAllowed)]
    [InlineData("/test/task", HttpMethods.Options, StatusCodes.NoContent)]
    [InlineData("/test/error", HttpMethods.Get, StatusCodes.UnprocessableContent)]
    public async Task IndexFallbackOnlyHandlesUnmatchedGetAndHeadRequests(string path, string method, int status)
    {
        var root = CreateRoot();
        await File.WriteAllTextAsync(Path.Combine(root, "index.html"), "app shell");
        await using var app = new TestApplication(options =>
        {
            options.StaticFiles.DirectoryPath       = root;
            options.StaticFiles.FallbackToIndexHtml = true;
        });
        var response = await app.SendAsync(path, method);
        Assert.Equal(status, response.StatusCode);
        Assert.DoesNotContain("app shell", response.Text);
    }

    [Theory]
    [InlineData("/../outside.txt")]
    [InlineData("/folder%2f..%2foutside.txt")]
    [InlineData("/folder%5c..%5coutside.txt")]
    [InlineData("/.secret.txt")]
    [InlineData("/source.cs")]
    [InlineData("/")]
    [InlineData("/nested/")]
    public async Task PrivatePathsAndDirectoriesAreNotServed(string path)
    {
        var root = CreateRoot();
        Directory.CreateDirectory(Path.Combine(root, "nested"));
        await File.WriteAllTextAsync(Path.Combine(root, ".secret.txt"), "secret");
        await File.WriteAllTextAsync(Path.Combine(root, "source.cs"), "source");
        await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(root)!, "outside.txt"), "outside");
        await using var app = new TestApplication(options => options.StaticFiles.DirectoryPath = root);
        Assert.Equal(StatusCodes.NotFound, (await app.SendAsync(path)).StatusCode);
    }

    [Theory]
    [InlineData(TrailingSlashBehavior.Ignore, 200)]
    [InlineData(TrailingSlashBehavior.Strict, 404)]
    [InlineData(TrailingSlashBehavior.RedirectToWithoutSlash, 308)]
    public async Task StaticFilesFollowTheInstanceSlashPolicy(TrailingSlashBehavior policy, int status)
    {
        var root = CreateRoot();
        await File.WriteAllTextAsync(Path.Combine(root, "hello.txt"), "hello");
        await using var app = new TestApplication(options =>
        {
            options.StaticFiles.DirectoryPath = root;
            options.TrailingSlashes           = policy;
        });
        Assert.Equal(status, (await app.SendAsync("/hello.txt/")).StatusCode);
    }

    [SymlinkFact]
    public async Task LinksBelowThePublicDirectoryCannotBeServedAsFilesOrIndexFallbacks()
    {
        var root    = CreateRoot();
        var outside = Path.Combine(Path.GetDirectoryName(root)!, "private");
        Directory.CreateDirectory(outside);
        var secret = Path.Combine(outside, "secret.txt");
        await File.WriteAllTextAsync(secret, "private data");
        File.CreateSymbolicLink(Path.Combine(root, "linked.txt"), secret);
        Directory.CreateSymbolicLink(Path.Combine(root, "linked"), outside);
        File.CreateSymbolicLink(Path.Combine(root, "index.html"), secret);
        await using var app = new TestApplication(options =>
        {
            options.StaticFiles.DirectoryPath       = root;
            options.StaticFiles.FallbackToIndexHtml = true;
        });
        Assert.Equal(StatusCodes.NotFound, (await app.SendAsync("/linked.txt")).StatusCode);
        Assert.Equal(StatusCodes.NotFound, (await app.SendAsync("/linked/secret.txt")).StatusCode);
        Assert.Equal(StatusCodes.NotFound, (await app.SendAsync("/")).StatusCode);
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "serval-static-" + Guid.NewGuid().ToString("N"), "public");
        Directory.CreateDirectory(root);
        return root;
    }

    private sealed class SymlinkFactAttribute : FactAttribute
    {
        public SymlinkFactAttribute()
        {
            try
            {
                var root   = CreateRoot();
                var target = Path.Combine(root, "target.txt");
                File.WriteAllText(target, "probe");
                File.CreateSymbolicLink(Path.Combine(root, "link.txt"), target);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                Skip = "This host does not permit symbolic link creation.";
            }
        }
    }
}
