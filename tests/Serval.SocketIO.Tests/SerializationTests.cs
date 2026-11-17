using System.Text.Json;
using System.Text.Json.Serialization;
using Serval.SocketIO.Internal;

namespace Serval.SocketIO.Tests;

public sealed class SerializationTests
{
    [Fact]
    public void ConcurrentMessagesKeepTheirOwnAttachmentsAndSerializerSettings()
    {
        var options = new SocketIOOptions();
        options.Json.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
        options.Validate();
        Parallel.For(0, 1000, index =>
        {
            var value = new Payload
            {
                PacketData = BitConverter.GetBytes(index),
                NestedData = [BitConverter.GetBytes(~index)]
            };
            var packets = PacketCodec.Encode(2, "/", index, new object[] { "binary", value }, options);
            Assert.Contains("packet_data", packets[0].PollingText);
            var decoded   = PacketCodec.Decode(packets[0].PollingText[1..], options);
            var arguments = new SocketIOArguments(decoded.Data, [.. packets.Skip(1).Select(packet => packet.Data)], options.Json, 1);
            var result    = arguments.Get<Payload>(0)!;
            Assert.Equal(value.PacketData, result.PacketData);
            Assert.Equal(value.NestedData[0], result.NestedData[0]);
            result.PacketData[0] ^= 255;
            Assert.Equal(value.PacketData, arguments.Get<Payload>(0)!.PacketData);
        });
    }

    [Fact]
    public void FailedSerializationDoesNotRetainAttachmentsOrByteCounts()
    {
        var options = new SocketIOOptions
        {
            MaxAttachments  = 1,
            MaxPayloadBytes = 100
        };
        for (var index = 0; index < 10; index++)
        {
            Assert.Throws<InvalidOperationException>(() => PacketCodec.Encode(2, "/", null, new object[] { "binary", new byte[10], new byte[10] }, options));
            var packets = PacketCodec.Encode(2, "/", null, new object[] { "binary", new byte[] { 42 } }, options);
            Assert.Equal(2, packets.Length);
            Assert.Equal(new byte[] { 42 }, packets[1].Data);
            Assert.Contains("\"num\":0", packets[0].PollingText);
        }
    }

    [Fact]
    public void FailedDeserializationDoesNotLeakAttachmentsIntoTheNextMessage()
    {
        var       options = new SocketIOOptions();
        using var invalid = JsonDocument.Parse("[{\"_placeholder\":true,\"num\":1}]");
        using var valid   = JsonDocument.Parse("[{\"_placeholder\":true,\"num\":0}]");
        Assert.Throws<JsonException>(() => new SocketIOArguments(invalid.RootElement, [new byte[] { 1 }], options.Json).Get<byte[]>(0));
        Assert.Equal(new byte[] { 2 }, new SocketIOArguments(valid.RootElement, [new byte[] { 2 }], options.Json).Get<byte[]>(0));
        Assert.Throws<JsonException>(() => new SocketIOArguments(valid.RootElement, [], options.Json).Get<byte[]>(0));
    }

    [Fact]
    public void CustomConvertersCanSerializeAnotherPacketReentrantly()
    {
        var options = new SocketIOOptions();
        options.Json.Converters.Add(new ReentrantConverter(options));
        options.Validate();
        var packets = PacketCodec.Encode(2, "/", null, new object[] { "binary", new byte[] { 1 }, new ReentrantValue(), new byte[] { 2 } }, options);
        Assert.Equal(3, packets.Length);
        Assert.Equal(new byte[] { 1 }, packets[1].Data);
        Assert.Equal(new byte[] { 2 }, packets[2].Data);
        Assert.Contains("\"num\":1", packets[0].PollingText);
    }

    private sealed class Payload
    {
        public byte[]   PacketData { get; init; } = [];
        public byte[][] NestedData { get; init; } = [];
    }

    private sealed class ReentrantValue {}

    private sealed class ReentrantConverter : JsonConverter<ReentrantValue>
    {
        private readonly SocketIOOptions _options;

        internal ReentrantConverter(SocketIOOptions options)
        {
            _options = options;
        }

        public override ReentrantValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => throw new NotSupportedException();

        public override void Write(Utf8JsonWriter writer, ReentrantValue value, JsonSerializerOptions options)
        {
            var inner = PacketCodec.Encode(2, "/", null, new object[] { "inner", new byte[] { 99 } }, _options);
            Assert.Equal(2, inner.Length);
            Assert.Equal(new byte[] { 99 }, inner[1].Data);
            writer.WriteNumberValue(7);
        }
    }
}
