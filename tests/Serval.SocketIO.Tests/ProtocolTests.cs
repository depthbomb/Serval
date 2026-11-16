using System.Text.Json;
using Serval.SocketIO.Internal;

namespace Serval.SocketIO.Tests;

public sealed class ProtocolTests
{
    [Theory]
    [InlineData("0", 0, "/", null)]
    [InlineData("0/chat,{\"token\":\"test\"}", 0, "/chat", null)]
    [InlineData("2/chat,12[\"hello\",1]", 2, "/chat", 12L)]
    [InlineData("31[]", 3, "/", 1L)]
    [InlineData("51-/chat,0[\"binary\",{\"_placeholder\":true,\"num\":0}]", 5, "/chat", 0L)]
    [InlineData("1/chat,", 1, "/chat", null)]
    public void DecodesProtocolPackets(string encoded, int type, string ns, long? id)
    {
        var packet = PacketCodec.Decode(encoded, new SocketIOOptions());
        Assert.Equal(type, packet.Type);
        Assert.Equal(ns, packet.Namespace);
        Assert.Equal(id, packet.Id);
    }

    [Theory]
    [InlineData("")]
    [InlineData("9")]
    [InlineData("0[]")]
    [InlineData("01{}")]
    [InlineData("1{}")]
    [InlineData("2[]")]
    [InlineData("2[\"disconnect\"]")]
    [InlineData("2[null]")]
    [InlineData("3[]")]
    [InlineData("31{}")]
    [InlineData("29007199254740992[\"hello\"]")]
    [InlineData("533-[\"binary\"]")]
    [InlineData("5-1-[\"binary\"]")]
    [InlineData("4{\"message\":\"client cannot send connect_error\"}")]
    public void RejectsInvalidPackets(string encoded)
        => Assert.Throws<InvalidDataException>(() => PacketCodec.Decode(encoded, new SocketIOOptions()));

    [Fact]
    public void BinaryRoundTripPreservesNestedAttachmentsAndSnapshotsBuffers()
    {
        var options = new SocketIOOptions();
        var value = new BinaryEnvelope
        {
            Data   = [0, 128, 255],
            Nested = [[42], []]
        };
        var encoded = PacketCodec.Encode(2, "/chat", 7, new object[] { "binary", value }, options);
        Assert.StartsWith("453-/chat,7", encoded[0].PollingText);
        Assert.Equal(4, encoded.Length);
        Assert.All(encoded.Skip(1), packet => Assert.True(packet.Binary));
        value.Data[0] = 99;
        var packet  = PacketCodec.Decode(encoded[0].PollingText[1..], options);
        var args    = new SocketIOArguments(packet.Data, encoded.Skip(1).Select(part => part.Data).ToArray(), options.Json, 1);
        var decoded = args.Get<BinaryEnvelope>(0)!;
        Assert.Equal(new byte[] { 0, 128, 255 }, decoded.Data);
        Assert.Equal(new byte[] { 42 }, decoded.Nested[0]);
        Assert.Empty(decoded.Nested[1]);
    }

    [Theory]
    [InlineData("{\"_placeholder\":true,\"num\":-1}")]
    [InlineData("{\"_placeholder\":true,\"num\":1}")]
    [InlineData("{\"_placeholder\":true,\"num\":\"0\"}")]
    [InlineData("{\"_placeholder\":true}")]
    public void RejectsOutOfRangeBinaryPlaceholders(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Throws<InvalidDataException>(() => PacketCodec.ValidatePlaceholders(document.RootElement, 1));
    }

    [Fact]
    public void OutgoingLimitsIncludeBinaryDataAndBase64PollingExpansion()
    {
        var options = new SocketIOOptions
        {
            MaxPayloadBytes = 100
        };
        Assert.Throws<InvalidOperationException>(() => PacketCodec.Encode(2, "/", null, new object[] { "event", new byte[100] }, options));
        options.MaxPayloadBytes = 200;
        options.MaxAttachments  = 1;
        Assert.Throws<InvalidOperationException>(() => PacketCodec.Encode(2, "/", null, new object[] { "event", new byte[1], new byte[1] }, options));
    }

    [Fact]
    public void ConfigurationRejectsInvalidLimitsAndOrigins()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SocketIOOptions
        {
            PingInterval = TimeSpan.Zero
        }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new SocketIOOptions
        {
            MaxQueuedBytes = 0
        }.Validate());
        Assert.Throws<ArgumentException>(() => new SocketIOOptions
        {
            Path = "/{any}"
        }.Validate());
        var options = new SocketIOOptions();
        options.AllowedOrigins.Add("*");
        Assert.Throws<ArgumentException>(options.Validate);
    }
}
