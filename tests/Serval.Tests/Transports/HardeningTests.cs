#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Text;
using Serval.Hosting;
using Serval.Http;
using Serval.Http.Constants;
using Serval.Http.Results;
using Serval.Routing;
using Serval.Tests.Support;
using H2 = Serval.Transport.Http2.Protocol;
using H3 = Serval.Transport.Http3.Protocol;

namespace Serval.Tests.Transports;

public sealed class HardeningTests
{
    [Theory]
    [InlineData("content-length", "3")]
    [InlineData("host", "other.example")]
    [InlineData("transfer-encoding", "chunked")]
    [InlineData("connection", "close")]
    [InlineData("te", "trailers")]
    [InlineData("Upper", "value")]
    [InlineData("x-field", " value")]
    [InlineData("x-field", "value\t")]
    [InlineData("x-field", "a\r\ninjected: value")]
    [InlineData("x-field", "a\0b")]
    [InlineData(":path", "/other")]
    public async Task Http3RejectsMalformedTrailers(string name, string value)
    {
        using var wire = new MemoryStream();
        await H3.Frames.WriteAsync(wire, H3.Frames.Headers, FieldSection((name, value)), CancellationToken.None);
        wire.Position = 0;
        using var body  = new H3.Http3BodyStream(wire, 1024, 1024, 0);
        var       error = await Assert.ThrowsAsync<H3.Http3ProtocolException>(() => body.CopyToAsync(Stream.Null));
        Assert.Equal(H3.Http3Error.Message, error.ErrorCode);
    }

    [Fact]
    public async Task Http3AcceptsOrdinaryTrailersAfterData()
    {
        using var wire = new MemoryStream();
        await H3.Frames.WriteAsync(wire, H3.Frames.Data, "abc"u8.ToArray(), CancellationToken.None);
        await H3.Frames.WriteAsync(wire, H3.Frames.Headers, FieldSection(("x-checksum", "abc")), CancellationToken.None);
        wire.Position = 0;
        using var body   = new H3.Http3BodyStream(wire, 1024, 1024, 3);
        using var result = new MemoryStream();
        await body.CopyToAsync(result);
        Assert.Equal("abc", Encoding.UTF8.GetString(result.ToArray()));
    }

    [Theory]
    [InlineData("40")]
    [InlineData("8000")]
    [InlineData("c00000")]
    [InlineData("0140")]
    public async Task TruncatedHttp3FramePrefixesAreProtocolErrors(string hex)
    {
        using var wire  = new MemoryStream(Convert.FromHexString(hex));
        var       error = await Assert.ThrowsAsync<H3.Http3ProtocolException>(() => H3.Frames.ReadAsync(wire, CancellationToken.None).AsTask());
        Assert.Equal(H3.Http3Error.Frame, error.ErrorCode);
    }

    [Fact]
    public async Task TruncatedHttp3HeaderPayloadIsAProtocolError()
    {
        using var wire  = new MemoryStream([0]);
        var       error = await Assert.ThrowsAsync<H3.Http3ProtocolException>(() => H3.Frames.ReadPayloadAsync(wire, 2, 1024, CancellationToken.None).AsTask());
        Assert.Equal(H3.Http3Error.Frame, error.ErrorCode);
    }

    [Fact]
    public void QpackPreservesHeaderOctetsAndCountsWireBytes()
    {
        var fields = H3.QPack.Decode(FieldSection(("x", "\u00ff\u00c0\u0080")), 36);
        Assert.Equal(("x", "\u00ff\u00c0\u0080"), Assert.Single(fields));
        var headers = new Headers { ["x"] = "\u00ff\u00c0\u0080" };
        Assert.Contains(("x", "\u00ff\u00c0\u0080"), H3.QPack.Decode(H3.QPack.Encode(200, headers), 1024));
        headers["x"] = "\u0100";
        Assert.Throws<ArgumentException>(() => H3.QPack.Encode(200, headers));
    }

    [Theory]
    [InlineData("example.com", null, null)]
    [InlineData("example.com:", null, null)]
    [InlineData("example.com:443", "https", null)]
    [InlineData("example.com:443", null, "/")]
    [InlineData("example.com:443", "https", "/")]
    public void MalformedConnectTargetsAreRejected(string authority, string? scheme, string? path)
    {
        List<(string Name, string Value)> fields = [(":method", "CONNECT"), (":authority", authority)];
        if (scheme is not null)
        {
            fields.Add((":scheme", scheme));
        }

        if (path is not null)
        {
            fields.Add((":path", path));
        }

        Assert.Throws<H2.Http2ProtocolException>(() => H2.RequestHeaders.Parse(fields, 1, true));
        Assert.Throws<H3.Http3ProtocolException>(() => H3.RequestHeaders.Parse(fields));
    }

    [Theory]
    [InlineData("example.com:443")]
    [InlineData("[::1]:443")]
    public void ValidConnectTargetsArePreserved(string authority)
    {
        (string Name, string Value)[] fields = [(":method", "CONNECT"), (":authority", authority)];
        Assert.Equal("CONNECT", H2.RequestHeaders.Parse(fields, 1, true).Method);
        Assert.Equal("CONNECT", H3.RequestHeaders.Parse(fields).Method);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequestDeadlinesApplyToBodyReadsWithoutTheContextToken(bool supplyIndependentToken)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddServal(options => options.RequestTimeout = TimeSpan.FromMilliseconds(60)).AddController<object>("POST", "/", null, async (_, context) =>
        {
            using var independent = new CancellationTokenSource();
            await context.Request.ReadBodyAsync(supplyIndependentToken ? independent.Token : CancellationToken.None);
            return EmptyResult.Instance;
        });
        await using var provider = services.BuildServiceProvider();
        using var       body     = new StalledReadStream();
        var             request  = new Request("POST", new Uri("http://localhost/"), HttpVersion.Version11, new Headers(), body, 1024);
        var             response = new MemoryResponse();
        await provider.GetRequiredService<IRequestHandler>().HandleAsync(request, response, CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(StatusCodes.RequestTimeout, response.StatusCode);
    }

    [Fact]
    public async Task ConflictingRequestFramingIsRejectedBeforeDispatch()
    {
        await using var app = new TestApplication();
        var response = await app.SendAsync("/test/task", configureHeaders: headers =>
        {
            headers[HeaderNames.ContentLength] = "0";
            headers["Transfer-Encoding"]       = "chunked";
        });
        Assert.Equal(StatusCodes.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData(204, null)]
    [InlineData(200, "chunked")]
    public async Task ContradictoryResponseFramingFailsBeforeCommit(int status, string? transferEncoding)
    {
        var response = new MemoryResponse { StatusCode = status };
        response.Headers[HeaderNames.ContentLength] = "0";
        response.Headers["Transfer-Encoding"]       = transferEncoding;
        await Assert.ThrowsAsync<InvalidOperationException>(() => response.StartAsync().AsTask());
        Assert.False(response.HasStarted);
    }

    [Fact]
    public void EmptySegmentsDoNotBindRequiredParameters()
    {
        var route = new RouteTemplate("/a/{id}/b", new ServalOptions());
        Assert.False(route.TryMatch(["a", "", "b"], false, out _));
        Assert.Throws<ArgumentException>(() => new RouteTemplate("/a//", new ServalOptions()));
        var optional = new RouteTemplate("/a/{id=}", new ServalOptions());
        Assert.True(optional.TryMatch(["a"], false, out var values));
        Assert.Equal("", values["id"]);
    }

    [Fact]
    public async Task CancellationStopsFurtherRouteConstraintEvaluation()
    {
        using var cancellation = new CancellationTokenSource();
        var       calls        = 0;
        var       options      = new ServalOptions();
        options.RouteConstraints["probe"] = (_, _) =>
        {
            calls++;
            cancellation.Cancel();
            return false;
        };
        var registry = new ControllerRegistry();
        for (var index = 0; index < 3; index++)
        {
            registry.Add<object>("GET", $"/a/{{id:probe({index})}}", null, (_, context) => ValueTask.FromResult<IActionResult>(EmptyResult.Instance));
        }

        var       router   = new Router(registry, options);
        var       request  = new Request("GET", new Uri("http://localhost/a/value"), HttpVersion.Version11, new Headers(), Stream.Null, 1024);
        using var services = new ServiceCollection().BuildServiceProvider();
        var       context  = new RequestContext(request, new MemoryResponse(), services, router, cancellation.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => router.TryDispatchAsync(context).AsTask());
        Assert.Equal(1, calls);
    }

    [Fact]
    public void RandomCompressedHeadersStayWithinLimitsAndFailWithProtocolErrors()
    {
        var random = new Random(981744);
        for (var iteration = 0; iteration < 5000; iteration++)
        {
            var bytes = new byte[random.Next(0, 65)];
            random.NextBytes(bytes);
            try
            {
                var fields = new H2.HPack().Decode(bytes, 256);
                Assert.InRange(fields.Sum(field => field.Name.Length + field.Value.Length + 32), 0, 256);
            }
            catch (H2.Http2ProtocolException) { }

            try
            {
                var fields = H3.QPack.Decode([0, 0, .. bytes], 256);
                Assert.InRange(fields.Sum(field => field.Name.Length + field.Value.Length + 32), 0, 256);
            }
            catch (H3.Http3ProtocolException) { }
        }
    }

    [Fact]
    public async Task Win32PathAliasesCannotEscapeTheStaticRoot()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = Path.Combine(Path.GetTempPath(), "serval-alias-" + Guid.NewGuid().ToString("N"));
        var root      = Path.Combine(directory, "public");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(directory, "secret.txt"), "private");
        await File.WriteAllTextAsync(Path.Combine(root, "safe.txt"), "public");
        await using var app = new TestApplication(options =>
        {
            options.StaticFiles.DirectoryPath = root;
            options.StaticFiles.ServeDotFiles = true;
        });
        Assert.Equal(StatusCodes.NotFound, (await app.SendAsync("/..%20/secret.txt")).StatusCode);
        Assert.Equal(StatusCodes.NotFound, (await app.SendAsync("/safe.txt%20")).StatusCode);
        Assert.Equal("public", (await app.SendAsync("/safe.txt")).Text);
    }

    [Fact]
    public void PseudoHeadersCannotNormalizeDelIntoTheRequestPath()
    {
        (string Name, string Value)[] fields = [(":method", "GET"), (":scheme", "https"), (":authority", "example.com"), (":path", "/a\u007fb")];
        Assert.Throws<H2.Http2ProtocolException>(() => H2.RequestHeaders.Parse(fields, 1, true));
        Assert.Throws<H3.Http3ProtocolException>(() => H3.RequestHeaders.Parse(fields));
    }

    internal static byte[] FieldSection(params (string Name, string Value)[] fields)
    {
        List<byte> bytes = [0, 0];
        foreach (var (name, value) in fields)
        {
            WriteInteger(name.Length, 3, 0x30);
            bytes.AddRange(Encoding.Latin1.GetBytes(name));
            WriteInteger(value.Length, 7, 0);
            bytes.AddRange(Encoding.Latin1.GetBytes(value));
        }

        return bytes.ToArray();

        void WriteInteger(int value, int prefix, byte flags)
        {
            var mask = (1 << prefix) - 1;
            bytes.Add((byte)(flags | Math.Min(value, mask)));
            if (value < mask)
            {
                return;
            }

            value -= mask;
            while (value >= 128)
            {
                bytes.Add((byte)(value & 0x7f | 0x80));
                value >>= 7;
            }

            bytes.Add((byte)value);
        }
    }

    private sealed class StalledReadStream : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }
}
