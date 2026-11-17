#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Net;
using Serval.Http;
using Serval.Http.Constants;
using Serval.Tests.Support;

namespace Serval.Tests.Core.Http;

public sealed class HttpHelperTests
{
    [Fact]
    public void CookiesRemainSeparateAndHaveSecureDefaults()
    {
        var headers = new Headers();
        var cookies = new ResponseCookies(headers);
        cookies.Set("one", "a b");
        cookies.Set("two", "value");
        cookies.Delete("three");
        Assert.Equal(3, headers.GetValues(HeaderNames.SetCookie).Count);
        Assert.Contains("one=a%20b; Path=/; Secure; HttpOnly; SameSite=Lax", headers.GetValues(HeaderNames.SetCookie));
        Assert.Contains("Max-Age=0", headers.GetValues(HeaderNames.SetCookie)[2]);
    }

    [Theory]
    [InlineData("__Host-id", false, null, "/", SameSite.Lax)]
    [InlineData("__Host-id", true, "example.com", "/", SameSite.Lax)]
    [InlineData("__Secure-id", false, null, "/", SameSite.Lax)]
    [InlineData("id", false, null, "/", SameSite.None)]
    [InlineData("id", true, "bad;domain", "/", SameSite.Lax)]
    public void InvalidCookieOptionsFailBeforeCommit(string name, bool secure, string? domain, string path, SameSite sameSite)
    {
        var cookies = new ResponseCookies(new Headers());
        Assert.Throws<ArgumentException>(() => cookies.Set(name, "value", new CookieOptions
        {
            Secure   = secure,
            Domain   = domain,
            Path     = path,
            SameSite = sameSite
        }));
    }

    [Theory]
    [InlineData("bad\r\nname", "value")]
    [InlineData("bad:name", "value")]
    [InlineData("Good", "bad\r\nvalue")]
    public void HeaderInjectionIsRejected(string name, string value)
    {
        var headers = new Headers();
        Assert.Throws<ArgumentException>(() => headers[name] = value);
    }

    [Fact]
    public async Task HeadersAndStatusAreImmutableAfterStarting()
    {
        var response = new MemoryResponse();
        await response.StartAsync();
        Assert.Throws<InvalidOperationException>(() => response.Headers["X-Test"] = "no");
        Assert.Throws<InvalidOperationException>(() => response.StatusCode        = StatusCodes.Created);
    }

    [Fact]
    public async Task TextHelpersCanWriteMultipleChunks()
    {
        var response = new MemoryResponse();
        await response.WriteTextAsync("one");
        await response.WriteTextAsync("two");
        Assert.Equal("onetwo", response.Text);
        Assert.Equal(MimeTypes.Text, response.Headers[HeaderNames.ContentType]);
    }

    [Fact]
    public async Task SseHandlesMultilineDataAndRejectsFieldInjection()
    {
        var response = new MemoryResponse();
        var events   = await response.StartEventsAsync();
        await events.SendAsync("first\r\nsecond", "tick", "42", TimeSpan.FromSeconds(1));
        Assert.Equal("event: tick\nid: 42\nretry: 1000\ndata: first\ndata: second\n\n", response.Text);
        Assert.Equal(MimeTypes.EventStream, response.Headers[HeaderNames.ContentType]);
        await Assert.ThrowsAsync<ArgumentException>(() => events.SendAsync("data", "bad\nevent").AsTask());
    }

    [Fact]
    public void RequestCookiesAndQueryValuesAreDecoded()
    {
        var headers = new Headers();
        headers.Append(HeaderNames.Cookie, "one=a%20b; two=c=d");
        var request = new Request(HttpMethods.Get, new Uri("http://localhost/??name=value&a=1&A=2&a=3&empty&=blank&&q=a%2Bb+%26&x=1=2&%61=4"), HttpVersion.Version11, headers, Stream.Null, 1024);
        Assert.Equal("a b", request.Cookies["one"]);
        Assert.Equal("c=d", request.Cookies["two"]);
        Assert.Equal(["1", "2", "3", "4"], request.Query["a"]);
        Assert.Equal("", Assert.Single(request.Query["empty"]));
        Assert.Equal("blank", Assert.Single(request.Query[""]));
        Assert.Equal("a+b &", Assert.Single(request.Query["q"]));
        Assert.Equal("1=2", Assert.Single(request.Query["x"]));
        Assert.Equal("value", Assert.Single(request.Query["?name"]));
        Assert.False(request.Query.ContainsKey("name"));
    }
}
