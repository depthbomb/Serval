#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Serval.Transport.Http3.Protocol;

namespace Serval.Tests.Transports.Http3;

public sealed class Http3ProtocolTests
{
    [Theory]
    [InlineData(0L)]
    [InlineData(63L)]
    [InlineData(64L)]
    [InlineData(16383L)]
    [InlineData(16384L)]
    [InlineData(1073741823L)]
    [InlineData(1073741824L)]
    [InlineData(4611686018427387903L)]
    public async Task QuicIntegerBoundariesRoundTrip(long value)
    {
        using var stream = new MemoryStream(Frames.Integer(value));
        Assert.Equal(value, await Frames.ReadIntegerAsync(stream, CancellationToken.None));
    }

    [Theory]
    [InlineData("ff")]
    [InlineData("ffffffff")]
    [InlineData("00")]
    public void InvalidHuffmanPaddingOrEosIsRejected(string encoded)
    {
        Assert.Throws<Http3ProtocolException>(() => Huffman.Decode(Convert.FromHexString(encoded), 1024));
    }

    [Fact]
    public void QpackDecodesStaticAndHuffmanHeaders()
    {
        Assert.Equal("www.example.com", Huffman.Decode(Convert.FromHexString("f1e3c2e5f23a6ba0ab90f4ff"), 1024));
        var encoded = Convert.FromHexString("0000d1d7508cf1e3c2e5f23a6ba0ab90f4ffc1");
        var fields  = QPack.Decode(encoded, 1024);
        Assert.Contains((":method", "GET"), fields);
        Assert.Contains((":scheme", "https"), fields);
        Assert.Contains((":authority", "www.example.com"), fields);
        Assert.Contains((":path", "/"), fields);
    }

    [Theory]
    [InlineData("", 1024)]
    [InlineData("00", 1024)]
    [InlineData("0100", 1024)]
    [InlineData("0080", 1024)]
    [InlineData("0000ff7f", 1024)]
    [InlineData("000080", 1024)]
    [InlineData("000050ff", 1024)]
    [InlineData("0000d1d7c1", 20)]
    public void InvalidOrOversizedQpackIsRejected(string payload, int limit)
    {
        Assert.Throws<Http3ProtocolException>(() => QPack.Decode(Convert.FromHexString(payload), limit));
    }

    [Fact]
    public async Task BodyStreamRejectsContentLengthMismatch()
    {
        using var wire = new MemoryStream();
        await Frames.WriteAsync(wire, Frames.Data, "abc"u8.ToArray(), CancellationToken.None);
        wire.Position = 0;
        var body = new Http3BodyStream(wire, 1024, 1024, 4);
        await Assert.ThrowsAsync<Http3ProtocolException>(() => body.CopyToAsync(Stream.Null));
    }
}
