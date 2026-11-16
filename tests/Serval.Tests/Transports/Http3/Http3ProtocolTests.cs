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
    [InlineData(30, "accept", "application/dns-message")]
    [InlineData(31, "accept-encoding", "gzip, deflate, br")]
    [InlineData(36, "cache-control", "max-age=0")]
    [InlineData(41, "cache-control", "public, max-age=31536000")]
    [InlineData(44, "content-type", "application/dns-message")]
    [InlineData(45, "content-type", "application/javascript")]
    [InlineData(52, "content-type", "text/html; charset=utf-8")]
    [InlineData(53, "content-type", "text/plain")]
    [InlineData(54, "content-type", "text/plain;charset=utf-8")]
    public void StaticTablePreservesRfc9204FieldValues(int index, string name, string value)
    {
        // A zero dynamic-table prefix followed by an indexed static field from RFC 9204 Appendix A.
        var fields = QPack.Decode([0, 0, (byte)(0xc0 | index)], 1024);
        Assert.Equal((name, value), Assert.Single(fields));
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
