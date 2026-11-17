#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Serialization;
using Serval.Http;
using Serval.Http.Constants;

namespace Serval.Tests.Core.Http;

public sealed class FormDataTests
{
    [Theory]
    [InlineData("urlencoded")]
    [InlineData("multipart")]
    [InlineData("json")]
    public async Task PostValuesShareConversionsNamesRepeatedValuesAndCaching(string format)
    {
        var body = format switch
        {
            "urlencoded" => "name=hello+%26+%E7%8B%B8&count=42&price=1.25&enabled=true&ids=1&ids=2&empty=&day=friday",
            "json"       => """{"name":"hello & 狸","count":42,"price":1.25,"enabled":true,"ids":[1,2],"empty":"","day":"friday"}""",
            _            => Multipart(("name", "hello & 狸"), ("count", "42"), ("price", "1.25"), ("enabled", "true"), ("ids", "1"), ("ids", "2"), ("empty", ""), ("day", "friday")),
        };
        var             contentType = format switch { "urlencoded" => MimeTypes.FormUrlEncoded, "json" => MimeTypes.Json, _ => "multipart/form-data; boundary=example" };
        await using var request     = Request(Encoding.UTF8.GetBytes(body), contentType);
        var             prior       = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var post = await request.ReadPostAsync();
            Assert.Same(post, await request.ReadPostAsync());
            Assert.Equal("hello & 狸", post.Get<string>("NAME"));
            Assert.Equal(42, await request.PostValueAsync<int>("count"));
            Assert.Equal(1.25m, post.Get<decimal>("price"));
            Assert.True(post.Get<bool>("enabled"));
            Assert.Equal(new[] { 1, 2 }, await request.PostValuesAsync<int>("ids"));
            Assert.Equal(DayOfWeek.Friday, post.GetEnum<DayOfWeek>("day"));
            Assert.Equal("", post.Get<string>("empty"));
            Assert.Equal(7, post.Get("missing", 7));
            Assert.Empty(post.GetValues<int>("missing"));
            Assert.False(post.TryGet<int>("missing", out _));
            Assert.False(post.TryGet<int>("ids", out _));
            Assert.False(post.TryGet<int>("name", out _));
            Assert.Equal(400, Assert.Throws<HttpErrorException>(() => post.Get<int>("ids")).StatusCode);
            Assert.Equal(400, Assert.Throws<HttpErrorException>(() => post.Get("name", 9)).StatusCode);
            Assert.Contains("name", post.Names);
            Assert.Empty(post.Files);
            if (format != "json")
            {
                Assert.Same(post, await request.ReadFormAsync());
            }

            await Assert.ThrowsAsync<InvalidOperationException>(() => request.ReadBodyAsync().AsTask());
        }
        finally
        {
            CultureInfo.CurrentCulture = prior;
        }
    }

    [Fact]
    public async Task JsonObjectsAndArraysUseExplicitMetadataAndRejectNullScalars()
    {
        await using var request = Request("""{"profile":{"name":"reese","aliases":["one","two"]},"null":null,"ids":[1,null],"empty":[]}""", MimeTypes.Json);
        var             profile = await request.PostValueAsync("profile", FormTestJsonContext.Default.FormProfile);
        Assert.Equal("reese", profile.Name);
        Assert.Equal(new[] { "one", "two" }, profile.Aliases);
        var data = await request.ReadPostAsync();
        Assert.False(data.TryGet<string>("profile", out _));
        Assert.True(data.ContainsKey("null"));
        Assert.Equal(400, Assert.Throws<HttpErrorException>(() => data.Get("null", "fallback")).StatusCode);
        Assert.Equal(400, Assert.Throws<HttpErrorException>(() => data.GetValues<int>("ids")).StatusCode);
        Assert.Empty(data.GetValues<int>("empty"));
        Assert.Equal(415, (await Assert.ThrowsAsync<HttpErrorException>(() => request.ReadFormAsync().AsTask())).StatusCode);
    }

    [Theory]
    [InlineData("application/json", "null", 400)]
    [InlineData("application/json", "[]", 400)]
    [InlineData("application/json", "", 400)]
    [InlineData("application/json", "{broken", 400)]
    [InlineData("application/json", "{\"a\":1,\"A\":2}", 400)]
    [InlineData("application/json", "{\"a\":{\"x\":1,\"x\":2}}", 400)]
    [InlineData("application/x-www-form-urlencoded", "x=%", 400)]
    [InlineData("application/x-www-form-urlencoded", "x=%G0", 400)]
    [InlineData("application/x-www-form-urlencoded", "x=%ff", 400)]
    [InlineData("application/x-www-form-urlencoded; charset=iso-8859-1", "x=ok", 415)]
    [InlineData("text/plain", "x=1", 415)]
    [InlineData(null, "x=1", 415)]
    [InlineData("multipart/form-data", "", 400)]
    [InlineData("multipart/form-data; boundary=x; boundary=y", "", 400)]
    [InlineData("multipart/form-data; boundary=x", "--x\r\nContent-Disposition: form-data; name=\"a\"\r\n\r\nmissing-end", 400)]
    public async Task InvalidPayloadsFailCleanly(string? type, string body, int status)
    {
        await using var request = Request(body, type);
        Assert.Equal(status, (await Assert.ThrowsAsync<HttpErrorException>(() => request.ReadPostAsync().AsTask())).StatusCode);
        Assert.Equal(status, (await Assert.ThrowsAsync<HttpErrorException>(() => request.ReadPostAsync().AsTask())).StatusCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(32600)]
    [InlineData(32700)]
    [InlineData(65536)]
    public async Task MultipartFilesKeepBinaryDataIndependentReadersAndRequestLifetime(int padding)
    {
        var directory = Path.Combine(Path.GetTempPath(), "serval-form-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var multipart = new MultipartFormDataContent("example");
        multipart.Add(new StringContent("description"), "caption");
        var bytes = Enumerable.Repeat((byte)0xff, padding).Concat("\0\r\n--exampleXYZ\r\n--example--NO\r\nend"u8.ToArray()).ToArray();
        multipart.Add(new ByteArrayContent(bytes), "files", "../../unsafe.bin");
        multipart.Add(new ByteArrayContent([]), "files", "empty.bin");
        var request = Request(await multipart.ReadAsByteArrayAsync(), multipart.Headers.ContentType!.ToString());
        request.FormOptions.MemoryThreshold = 8;
        request.FormOptions.TempDirectory   = directory;
        var data = await request.ReadFormAsync();
        Assert.Equal("description", data.Get<string>("caption"));
        Assert.Equal(2, data.GetFiles("FILES").Count);
        Assert.Throws<HttpErrorException>(() => data.GetFile("files"));
        Assert.Null(data.GetFile("absent"));
        Assert.Equal(bytes.Length, data.Files[0].Length);
        Assert.Equal("../../unsafe.bin", data.Files[0].FileName);
        Assert.Single(Directory.GetFiles(directory));
        Assert.Equal(0, data.Files[1].Length);
        await using var first  = data.Files[0].OpenReadStream();
        await using var second = data.Files[0].OpenReadStream();
        Assert.Equal(bytes[0], first.ReadByte());
        using var copied = new MemoryStream();
        await second.CopyToAsync(copied);
        Assert.Equal(bytes, copied.ToArray());
        first.Position = 0;
        Assert.Equal(bytes[0], first.ReadByte());
        Assert.False(first.CanWrite);
        await request.DisposeAsync();
        Assert.Empty(Directory.GetFiles(directory));
        Assert.Throws<ObjectDisposedException>(() => first.ReadByte());
    }

    [Theory]
    [InlineData("fields")]
    [InlineData("keys")]
    [InlineData("values")]
    [InlineData("files")]
    [InlineData("file-bytes")]
    [InlineData("headers")]
    [InlineData("header-bytes")]
    [InlineData("body")]
    public async Task MultipartLimitsRejectAndRemoveTemporaryFiles(string limit)
    {
        var directory = Path.Combine(Path.GetTempPath(), "serval-form-limits-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var multipart = new MultipartFormDataContent("example");
        multipart.Add(new StringContent("long value"), "long-name");
        multipart.Add(new StringContent("second"), "second");
        multipart.Add(new ByteArrayContent(new byte[32]), "file", "item.bin");
        await using var request = Request(await multipart.ReadAsByteArrayAsync(), multipart.Headers.ContentType!.ToString(), limit == "body" ? 8 : 1000000);
        request.FormOptions.MemoryThreshold = 1;
        request.FormOptions.TempDirectory   = directory;
        switch (limit)
        {
            case "fields":
                request.FormOptions.MaxFields = 1;
                break;
            case "keys":
                request.FormOptions.MaxKeyLength = 2;
                break;
            case "values":
                request.FormOptions.MaxValueLength = 2;
                break;
            case "files":
                request.FormOptions.MaxFiles = 0;
                break;
            case "file-bytes":
                request.FormOptions.MaxFileBytes = 2;
                break;
            case "headers":
                request.FormOptions.MaxMultipartHeaders = 1;
                break;
            case "header-bytes":
                request.FormOptions.MaxMultipartHeaderBytes = 8;
                break;
        }

        Assert.Equal(413, (await Assert.ThrowsAsync<HttpErrorException>(() => request.ReadFormAsync().AsTask())).StatusCode);
        Assert.Empty(Directory.GetFiles(directory));
    }

    [Theory]
    [InlineData("application/x-www-form-urlencoded", "a=1&a=2")]
    [InlineData("application/json", "{\"a\":[1,2]}")]
    public async Task RepeatedValuesCountTowardFieldLimit(string type, string body)
    {
        await using var request = Request(body, type);
        request.FormOptions.MaxFields = 1;
        Assert.Equal(413, (await Assert.ThrowsAsync<HttpErrorException>(() => request.ReadPostAsync().AsTask())).StatusCode);
    }

    [Fact]
    public async Task RawStreamingKeepsOwnershipLimitsAndReadModesExplicit()
    {
        using var input = new MemoryStream([.. "body"u8]);
        await using var request = new Request("POST", new Uri("http://localhost/"), HttpVersion.Version11,
            new Headers
            {
                [HeaderNames.ContentType] = MimeTypes.FormUrlEncoded
            }, input, 100);
        using var output = new MemoryStream();
        await request.CopyBodyToAsync(output);
        Assert.Equal("body"u8.ToArray(), output.ToArray());
        Assert.True(output.CanWrite);
        await Assert.ThrowsAsync<InvalidOperationException>(() => request.ReadPostAsync().AsTask());
        await request.DisposeAsync();
        Assert.True(input.CanRead);

        await using var tooLarge = Request("12345", MimeTypes.OctetStream, 4);
        Assert.Equal(413, (await Assert.ThrowsAsync<HttpErrorException>(() => tooLarge.CopyBodyToAsync(Stream.Null).AsTask())).StatusCode);
        Assert.Equal(413, (await Assert.ThrowsAsync<HttpErrorException>(() => tooLarge.CopyBodyToAsync(Stream.Null).AsTask())).StatusCode);
    }

    [Fact]
    public async Task CancellationBeforeReadingDoesNotConsumeTheBody()
    {
        await using var request      = Request("name=valid", MimeTypes.FormUrlEncoded);
        using var       cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request.ReadPostAsync(cancellation.Token).AsTask());
        Assert.Equal("valid", await request.PostValueAsync<string>("name"));
    }

    [Fact]
    public async Task DisposingAnIncompleteParseCancelsReadingAndDeletesItsBuffer()
    {
        var directory = Path.Combine(Path.GetTempPath(), "serval-form-cancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        await using var input = new WaitingBodyStream();
        var request = new Request("POST", new Uri("http://localhost/"), HttpVersion.Version11,
            new Headers
            {
                [HeaderNames.ContentType] = MimeTypes.FormUrlEncoded
            }, input, 100000);
        request.FormOptions.MemoryThreshold = 1;
        request.FormOptions.TempDirectory   = directory;
        var pending = request.ReadPostAsync().AsTask();
        var shared  = request.ReadPostAsync().AsTask();
        await input.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(Directory.GetFiles(directory));
        await request.DisposeAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => shared);
        Assert.Empty(Directory.GetFiles(directory));
        Assert.Equal(2, input.Reads);
    }

    [Theory]
    [InlineData("--example--")]
    [InlineData("--example--\r\n")]
    public async Task EmptyMultipartFormsAcceptClosingBoundaryAtEndOfBody(string body)
    {
        await using var request = Request(body, "multipart/form-data; boundary=example");
        var             form    = await request.ReadFormAsync();
        Assert.Empty(form.Names);
        Assert.Empty(form.Files);
    }

    [Theory]
    [InlineData("Content-Disposition: form-data; name=\"a\"\r\nContent-Disposition: form-data; name=\"b\"")]
    [InlineData("Content-Disposition: form-data; name=\"a\"; name=\"b\"")]
    [InlineData("Content-Disposition: attachment; name=\"a\"")]
    [InlineData("Content-Disposition: form-data; name=\"a\"\r\nContent-Transfer-Encoding: base64")]
    [InlineData("Content-Disposition: form-data; name=\"a\"\r\n folded: header")]
    public async Task AmbiguousMultipartHeadersAreRejected(string headers)
    {
        await using var request = Request("--example\r\n" + headers + "\r\n\r\nvalue\r\n--example--\r\n", "multipart/form-data; boundary=example");
        Assert.Equal(400, (await Assert.ThrowsAsync<HttpErrorException>(() => request.ReadFormAsync().AsTask())).StatusCode);
    }

    [Fact]
    public async Task ExtendedFilenamesAndFinalBoundariesWithoutNewlinesWork()
    {
        const string    body    = "--example\r\nContent-Disposition: form-data; name=\"file\"; filename=\"fallback\"; filename*=utf-8''%22%E7%8B%B8%22.txt\r\n\r\ndata\r\n--example--";
        await using var request = Request(body, "multipart/form-data; boundary=\"example\"");
        var             file    = Assert.Single((await request.ReadFormAsync()).Files);
        Assert.Equal("\"狸\".txt", file.FileName);
        using var output = new MemoryStream();
        await file.CopyToAsync(output);
        Assert.Equal("data"u8.ToArray(), output.ToArray());
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(33000, false)]
    public async Task MultipartBoundaryPaddingIsAcceptedAndBounded(int length, bool endAtEof)
    {
        var padding = "\t" + new string(' ', length);
        var body = "--example \t\r\nContent-Disposition: form-data; name=\"a\"\r\n\r\none\r\n--example"                                                                              + padding
                                                                                                                                                                                     + "\r\nContent-Disposition: form-data; name=\"b\"\r\n\r\ntwo\r\n--example--" + padding + (endAtEof ? "" : "\r\nepilogue");
        await using var request = Request(body, "multipart/form-data; boundary=example");
        request.FormOptions.MaxMultipartHeaderBytes = 40000;
        var form = await request.ReadFormAsync();
        Assert.Equal("one", form.Get<string>("a"));
        Assert.Equal("two", form.Get<string>("b"));
        if (length > 100)
        {
            await using var limited = Request(body, "multipart/form-data; boundary=example");
            limited.FormOptions.MaxMultipartHeaderBytes = 100;
            Assert.Equal(413, (await Assert.ThrowsAsync<HttpErrorException>(() => limited.ReadFormAsync().AsTask())).StatusCode);
        }
    }

    internal static string Multipart(params (string Name, string Value)[] values)
        => string.Concat(values.Select(value => $"--example\r\nContent-Disposition: form-data; name=\"{value.Name}\"\r\n\r\n{value.Value}\r\n")) + "--example--\r\n";

    private static Request Request(string body, string? type, long limit = 1000000) => Request(Encoding.UTF8.GetBytes(body), type, limit);

    private static Request Request(byte[] body, string? type, long limit = 1000000)
        => new("POST", new Uri("http://localhost/"), HttpVersion.Version11, new Headers
        {
            [HeaderNames.ContentType] = type
        }, new MemoryStream(body), limit);

    private sealed class WaitingBodyStream : Stream
    {
        public override bool CanRead  => true;
        public override bool CanSeek  => false;
        public override bool CanWrite => false;
        public override long Length   => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int                  Reads   { get; private set; }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (++Reads == 1)
            {
                buffer.Span.Fill((byte)'x');
                return buffer.Length;
            }

            Waiting.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush() {}

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

public sealed record FormProfile(string? Name, string[]? Aliases);

[JsonSerializable(typeof(FormProfile))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal partial class FormTestJsonContext : JsonSerializerContext;
