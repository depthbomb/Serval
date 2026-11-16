using System.Net;
using System.Text;
using Serval.Http;

namespace Serval.Tests.Core.Http;

public sealed class MultipartStreamingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidHeaderControlsReturnBadRequestInBothModes(bool streaming)
    {
        using var       input   = new MemoryStream([.. "--test\r\nContent-Disposition: form-data; name=file; filename=test\r\nX-Test: bad\u007fvalue\r\n\r\ndata\r\n--test--\r\n"u8]);
        await using var request = Create(input);
        var error = await Assert.ThrowsAsync<HttpErrorException>(async () =>
        {
            if (streaming)
            {
                await request.ReadMultipartAsync((_, _) => ValueTask.CompletedTask);
            }
            else
            {
                await request.ReadFormAsync();
            }
        });
        Assert.Equal(400, error.StatusCode);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(17)]
    [InlineData(32768)]
    public async Task SectionsPreserveBinaryDataAndFalseBoundaryPrefixes(int chunkSize)
    {
        byte[]          payload  = [0, 255, .. "hello\r\n--testXYZ\r\n--test--oops"u8, 128];
        await using var input    = new ChunkStream([.. "--test\r\nContent-Disposition: form-data; name=title\r\n\r\né\r\n--test\r\nContent-Disposition: form-data; name=file; filename=sample.bin\r\nContent-Type: application/octet-stream\r\n\r\n"u8, .. payload, .. "\r\n--test--\r\n"u8], chunkSize);
        await using var request  = Create(input);
        var             sections = new List<(string Name, string? FileName, byte[] Bytes)>();
        Stream?         retained = null;
        await request.ReadMultipartAsync(async (section, ct) =>
        {
            retained = section.Body;
            using var destination = new MemoryStream();
            await section.Body.CopyToAsync(destination, ct);
            sections.Add((section.Name, section.FileName, destination.ToArray()));
        });
        Assert.Equal(2, sections.Count);
        Assert.Equal("é", Encoding.UTF8.GetString(sections[0].Bytes));
        Assert.Equal("sample.bin", sections[1].FileName);
        Assert.Equal(payload, sections[1].Bytes);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await retained!.ReadExactlyAsync(new byte[1]));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await request.ReadBodyAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await request.ReadFormAsync());
    }

    [Fact]
    public async Task StreamingStartsBeforeTheBodyFinishesAndDoesNotUseTemporaryStorage()
    {
        var             bytes   = new byte[2 * 1024 * 1024];
        await using var input   = new ChunkStream(FileBody(bytes), 4096);
        await using var request = Create(input, input.Length + 1);
        request.FormOptions.MemoryThreshold = 0;
        request.FormOptions.TempDirectory   = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing");
        long length = 0;
        await request.ReadMultipartAsync(async (section, ct) =>
        {
            Assert.True(input.Position < input.Length);
            var buffer = new byte[16384];
            int count;
            while ((count = await section.Body.ReadAsync(buffer, ct)) != 0)
            {
                length += count;
            }
        });
        Assert.Equal(bytes.Length, length);
        Assert.False(Directory.Exists(request.FormOptions.TempDirectory));
    }

    [Fact]
    public async Task SkippedAndPartiallyReadSectionsStillEnforceLimits()
    {
        using var       input   = new MemoryStream(FileBody([1, 2, 3, 4, 5]));
        await using var request = Create(input);
        request.FormOptions.MaxFileBytes = 4;
        var error = await Assert.ThrowsAsync<HttpErrorException>(async () => await request.ReadMultipartAsync((_, _) => ValueTask.CompletedTask));
        Assert.Equal(413, error.StatusCode);
    }

    [Fact]
    public async Task CatchingASectionFailureCannotBypassLimits()
    {
        using var       input   = new MemoryStream(FileBody([1, 2, 3, 4, 5]));
        await using var request = Create(input);
        request.FormOptions.MaxFileBytes = 4;
        var error = await Assert.ThrowsAsync<HttpErrorException>(async () => await request.ReadMultipartAsync(async (section, ct) =>
        {
            await Assert.ThrowsAsync<HttpErrorException>(async () => await section.Body.CopyToAsync(Stream.Null, ct));
        }));
        Assert.Equal(413, error.StatusCode);
    }

    [Theory]
    [InlineData("é", 200)]
    [InlineData("éé", 413)]
    public async Task StreamingFieldsEnforceDecodedCharacterLimits(string value, int expected)
    {
        await using var input   = new ChunkStream(Encoding.UTF8.GetBytes("--test\r\nContent-Disposition: form-data; name=title\r\n\r\n" + value + "\r\n--test--\r\n"), 1);
        await using var request = Create(input);
        request.FormOptions.MaxValueLength = 1;
        if (expected == 200)
        {
            await request.ReadMultipartAsync((_, _) => ValueTask.CompletedTask);
        }
        else
        {
            var error = await Assert.ThrowsAsync<HttpErrorException>(async () => await request.ReadMultipartAsync((_, _) => ValueTask.CompletedTask));
            Assert.Equal(expected, error.StatusCode);
        }
    }

    [Theory]
    [InlineData("--test--\r\n", 400)]
    [InlineData("\r\n--test--\r\n", 200)]
    public async Task ClosingBoundaryRequiresACompleteDelimiter(string ending, int status)
    {
        using var       input   = new MemoryStream(Encoding.UTF8.GetBytes("--test\r\nContent-Disposition: form-data; name=f; filename=f\r\n\r\ndata" + ending));
        await using var request = Create(input);
        if (status == 200)
        {
            await request.ReadMultipartAsync((_, _) => ValueTask.CompletedTask);
        }
        else
        {
            var error = await Assert.ThrowsAsync<HttpErrorException>(async () => await request.ReadMultipartAsync((_, _) => ValueTask.CompletedTask));
            Assert.Equal(status, error.StatusCode);
        }
    }

    [Fact]
    public async Task EpiloguesCountTowardTheRequestLimitAndCancellationStopsReads()
    {
        await using var input   = new ChunkStream([.. FileBody([1]), .. new byte[200]], 1);
        await using var request = Create(input, 150);
        Assert.Equal(413, (await Assert.ThrowsAsync<HttpErrorException>(async () => await request.ReadMultipartAsync((_, _) => ValueTask.CompletedTask))).StatusCode);

        using var       cts      = new CancellationTokenSource();
        using var       other    = new MemoryStream(FileBody(new byte[100]));
        await using var canceled = Create(other);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await canceled.ReadMultipartAsync((_, _) =>
        {
            cts.Cancel();
            return ValueTask.CompletedTask;
        }, cts.Token));
    }

    private static Request Create(Stream input, long limit = 4 * 1024 * 1024)
        => new("POST", new Uri("http://localhost/"), HttpVersion.Version11, new Headers { ["Content-Type"] = "multipart/form-data; boundary=test" }, input, limit);

    private static byte[] FileBody(byte[] bytes)
        => [.. "--test\r\nContent-Disposition: form-data; name=file; filename=test.bin\r\n\r\n"u8, .. bytes, .. "\r\n--test--\r\n"u8];

    private sealed class ChunkStream : MemoryStream
    {
        private readonly int _chunkSize;

        internal ChunkStream(byte[] bytes, int chunkSize) : base(bytes)
        {
            _chunkSize = chunkSize;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
            => base.ReadAsync(buffer[..Math.Min(buffer.Length, _chunkSize)], ct);
    }
}
