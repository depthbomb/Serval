using System.Net;
using System.Text;
using Serval.Http;

namespace Serval.Tests.Core.Http;

public sealed class BodyStreamTests
{
    [Fact]
    public async Task LeasePreservesOwnershipExclusivityAndCumulativeLimits()
    {
        using var transport = new MemoryStream("abcdef"u8.ToArray());
        await using var request = Create(transport, 4);
        await using (var body = request.OpenBodyStream())
        {
            Assert.Equal(3, await body.ReadAsync(new byte[3]));
            Assert.Throws<InvalidOperationException>(() => request.OpenBodyStream());
            await Assert.ThrowsAsync<InvalidOperationException>(() => request.ReadTextAsync().AsTask());
        }

        Assert.True(transport.CanRead);
        await using var remainder = request.OpenBodyStream();
        Assert.Equal(1, await remainder.ReadAsync(new byte[1]));
        var error = await Assert.ThrowsAsync<HttpErrorException>(() => remainder.ReadAsync(new byte[1]).AsTask());
        Assert.Equal(413, error.StatusCode);
    }

    [Fact]
    public async Task LeaseObservesRequestAndPerReadCancellationAndCannotMixWithForms()
    {
        using var transport = new MemoryStream("name=alice"u8.ToArray());
        await using var request = Create(transport, 100, "application/x-www-form-urlencoded");
        using var cts = new CancellationTokenSource();
        request.RequestAborted = cts.Token;
        await using (var body = request.OpenBodyStream())
        {
            using var readCts = new CancellationTokenSource();
            readCts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => body.ReadAsync(new byte[1], readCts.Token).AsTask());
            await Assert.ThrowsAsync<InvalidOperationException>(() => request.ReadFormAsync().AsTask());
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => body.ReadAsync(new byte[1]).AsTask());
        }

        Assert.Equal(0, transport.Position);
    }

    [Fact]
    public async Task ExternalStreamParserCanReadWithoutWholeBodyBuffering()
    {
        using var transport = new MemoryStream("one\ntwo\n"u8.ToArray());
        await using var request = Create(transport, 100);
        await using var body = request.OpenBodyStream();
        using var reader = new StreamReader(body, Encoding.UTF8, leaveOpen: true);
        Assert.Equal("one", await reader.ReadLineAsync());
        Assert.Equal("two", await reader.ReadLineAsync());
        Assert.Null(await reader.ReadLineAsync());
    }

    private static Request Create(Stream body, long limit, string contentType = "text/plain")
        => new("POST", new Uri("http://localhost/"), HttpVersion.Version11, new Headers { ["Content-Type"] = contentType }, body, limit);
}
