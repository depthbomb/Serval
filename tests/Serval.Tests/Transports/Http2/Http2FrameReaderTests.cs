#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Serval.Transport.Http2.Protocol;

namespace Serval.Tests.Transports.Http2;

public sealed class Http2FrameReaderTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(32768)]
    public async Task FragmentedAndCoalescedFramesSurviveBufferBoundaries(int chunkSize)
    {
        using var input   = new MemoryStream();
        var       lengths = new[] { 0, 8, Frames.MaximumPayload, 12000, Frames.MaximumPayload, 100, 0 };
        for (var index = 0; index < lengths.Length; index++)
        {
            await input.WriteAsync(Frames.Header(Frames.Data, 0, index + 1, lengths[index]));
            await input.WriteAsync(Enumerable.Repeat((byte)index, lengths[index]).ToArray());
        }

        using var stream = new FragmentedReadStream(input.ToArray(), chunkSize);
        var       reader = new FrameReader(stream);
        for (var index = 0; index < lengths.Length; index++)
        {
            var frame = (await reader.ReadAsync(CancellationToken.None))!.Value;
            Assert.Equal(index + 1, frame.StreamId);
            Assert.Equal(Frames.Data, frame.Type);
            Assert.Equal(Enumerable.Repeat((byte)index, lengths[index]), frame.Payload.ToArray());
            if (index == 0)
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadAsync(new CancellationToken(true)).AsTask());
            }
        }

        Assert.Null(await reader.ReadAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(12)]
    public async Task TruncatedHeadersAndPayloadsAreRejected(int available)
    {
        var       bytes  = Frames.Header(Frames.Data, 0, 1, 8).Concat(new byte[8]).Take(available).ToArray();
        using var stream = new MemoryStream(bytes);
        var       reader = new FrameReader(stream);
        await Assert.ThrowsAsync<EndOfStreamException>(() => reader.ReadAsync(CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task OversizedFramesAreRejectedBeforeReadingTheirPayload()
    {
        using var stream    = new MemoryStream(Frames.Header(Frames.Data, 0, 1, Frames.MaximumPayload + 1));
        var       reader    = new FrameReader(stream);
        var       exception = await Assert.ThrowsAsync<Http2ProtocolException>(() => reader.ReadAsync(CancellationToken.None).AsTask());
        Assert.Equal(Http2Error.FrameSize, exception.ErrorCode);
    }

    private sealed class FragmentedReadStream(byte[] bytes, int chunkSize) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => base.ReadAsync(buffer[..Math.Min(buffer.Length, chunkSize)], cancellationToken);
    }
}
