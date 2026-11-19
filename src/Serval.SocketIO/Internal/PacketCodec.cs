#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Serval.SocketIO.Internal;

internal sealed record EnginePacket(byte[] Data, bool Binary = false)
{
    internal string PollingText => Binary ? "b" + Convert.ToBase64String(Data) : Utf8.GetString(Data);

    internal                 long         PollingSize => Binary ? 1L + 4L * ((Data.Length + 2L) / 3) : Data.Length;
    internal static readonly UTF8Encoding Utf8 = new(false, true);

    internal static EnginePacket Text(string text) => new(Utf8.GetBytes(text));
}

internal sealed record SocketPacket(int Type, string Namespace, long? Id, JsonElement Data, int AttachmentCount);

internal static class PacketCodec
{
    private static readonly ConditionalWeakTable<SocketIOOptions, EncoderPool> Encoders       = new();
    private static readonly HashSet<string>                                    ReservedEvents = ["connect", "connect_error", "disconnect", "disconnecting", "newListener", "removeListener"];

    internal static SocketPacket Decode(string text, SocketIOOptions options)
    {
        if (text.Length == 0 || text[0] is < '0' or > '6')
        {
            throw new InvalidDataException("Invalid Socket.IO packet type.");
        }

        var type        = text[0] - '0';
        var offset      = 1;
        var attachments = 0;
        if (type is 5 or 6)
        {
            var end = text.IndexOf('-', offset);
            if (end < 0 || !int.TryParse(text.AsSpan(offset, end - offset), NumberStyles.None, CultureInfo.InvariantCulture, out attachments) || attachments < 0 || attachments > options.MaxAttachments)
            {
                throw new InvalidDataException("Invalid attachment count.");
            }

            offset = end + 1;
        }

        var ns = "/";
        if (offset < text.Length && text[offset] == '/')
        {
            var end = text.IndexOf(',', offset);
            if (end < 0)
            {
                end = text.Length;
            }

            ns     = text[offset..end];
            offset = Math.Min(end + 1, text.Length);
            if (ns.Length > 256 || ns.Any(char.IsControl))
            {
                throw new InvalidDataException("Invalid namespace.");
            }
        }

        long? id    = null;
        var   start = offset;
        while (offset < text.Length && char.IsAsciiDigit(text[offset]))
        {
            offset++;
        }

        if (offset > start)
        {
            if (!long.TryParse(text.AsSpan(start, offset - start), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed > 9_007_199_254_740_991)
            {
                throw new InvalidDataException("Invalid acknowledgement ID.");
            }

            id = parsed;
        }

        var data = default(JsonElement);
        if (offset < text.Length)
        {
            using var document = JsonDocument.Parse(text[offset..]);
            data = document.RootElement.Clone();
        }

        var valid = type switch
        {
            0      => id is null && data.ValueKind is JsonValueKind.Undefined or JsonValueKind.Object,
            1      => id is null && data.ValueKind == JsonValueKind.Undefined,
            2 or 5 => data.ValueKind               == JsonValueKind.Array && data.GetArrayLength() > 0 && data[0].ValueKind == JsonValueKind.String && IsEventName(data[0].GetString()),
            3 or 6 => id is not null                                      && data.ValueKind        == JsonValueKind.Array,
            _      => false,
        };
        return !valid ? throw new InvalidDataException("Invalid Socket.IO payload.") : new SocketPacket(type, ns, id, data, attachments);
    }

    internal static EnginePacket[] Encode(int type, string ns, long? id, object? data, SocketIOOptions options)
    {
        var pool    = Encoders.GetValue(options, static value => new EncoderPool(value));
        var context = pool.Rent();
        try
        {
            var attachments = context.Attachments;
            var json        = data is null ? "" : JsonSerializer.Serialize(data, context.Json);
            if (attachments.Count > 0)
            {
                type = type switch
                {
                    2 => 5,
                    3 => 6,
                    _ => throw new InvalidOperationException("Binary data requires an event or acknowledgement."),
                };
            }

            var prefix = "4" + type.ToString(CultureInfo.InvariantCulture);
            if (type is 5 or 6)
            {
                prefix += attachments.Count.ToString(CultureInfo.InvariantCulture) + "-";
            }

            if (ns != "/")
            {
                prefix += ns + ",";
            }

            prefix += id?.ToString(CultureInfo.InvariantCulture);
            var packets = new EnginePacket[attachments.Count + 1];
            packets[0] = EnginePacket.Text(prefix            + json);
            long size = packets[0].Data.Length;
            for (var index = 0; index < attachments.Count; index++)
            {
                var packet = new EnginePacket(attachments[index], true);
                if (packet.PollingSize > options.MaxPayloadBytes)
                {
                    throw new InvalidOperationException("The outgoing Socket.IO packet exceeds MaxPayloadBytes.");
                }

                packets[index + 1] =  packet;
                size               += packet.Data.Length;
            }

            return size > options.MaxPayloadBytes ? throw new InvalidOperationException("The outgoing Socket.IO packet exceeds MaxPayloadBytes.") : packets;
        }
        finally
        {
            pool.Return(context);
        }
    }

    internal static void ValidateEventName(string name)
    {
        if (!IsEventName(name))
        {
            throw new ArgumentException("An event name must be nonempty and cannot be a reserved Socket.IO event.", nameof(name));
        }
    }

    internal static void ValidatePlaceholders(JsonElement data, int count)
    {
        switch (data.ValueKind)
        {
            case JsonValueKind.Object when data.TryGetProperty("_placeholder", out var marker) && marker.ValueKind == JsonValueKind.True:
            {
                if (!data.TryGetProperty("num", out var number) || number.ValueKind != JsonValueKind.Number || !number.TryGetInt32(out var index) || index < 0 || index >= count)
                {
                    throw new InvalidDataException("Invalid binary placeholder.");
                }

                return;
            }

            case JsonValueKind.Object:
            {
                foreach (var property in data.EnumerateObject())
                {
                    ValidatePlaceholders(property.Value, count);
                }

                break;
            }

            case JsonValueKind.Array:
            {
                foreach (var element in data.EnumerateArray())
                {
                    ValidatePlaceholders(element, count);
                }

                break;
            }
        }
    }

    private static bool IsEventName(string? name) => !string.IsNullOrEmpty(name) && !ReservedEvents.Contains(name);

    // Each operation owns a converter and its attachment list. Reusing its options keeps
    // serializer metadata warm without sharing mutable state between concurrent sends.
    private sealed class EncoderPool
    {
        private readonly Stack<EncoderContext> _available = new();
        private readonly SocketIOOptions       _options;

        internal EncoderPool(SocketIOOptions options)
        {
            _options = options;
        }

        internal EncoderContext Rent()
        {
            lock (_available)
            {
                return _available.TryPop(out var context) ? context : new EncoderContext(_options);
            }
        }

        internal void Return(EncoderContext context)
        {
            context.Attachments.Clear();
            context.Converter.Reset();
            lock (_available)
            {
                if (_available.Count < Math.Max(2, Environment.ProcessorCount))
                {
                    _available.Push(context);
                }
            }
        }
    }

    private sealed class EncoderContext
    {
        internal List<byte[]>          Attachments { get; } = [];
        internal JsonSerializerOptions Json        { get; }
        internal AttachmentConverter   Converter   { get; }

        internal EncoderContext(SocketIOOptions options)
        {
            Converter = new AttachmentConverter(Attachments, options);
            Json      = new JsonSerializerOptions(options.Json);
            Json.Converters.Insert(0, Converter);
        }
    }

    private sealed class AttachmentConverter : JsonConverter<byte[]>
    {
        private long _bytes;

        private readonly List<byte[]>    _attachments;
        private readonly SocketIOOptions _options;

        public AttachmentConverter(List<byte[]> attachments, SocketIOOptions options)
        {
            _attachments = attachments;
            _options     = options;
        }

        public override byte[] Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => throw new NotSupportedException();

        public override void Write(Utf8JsonWriter writer, byte[] value, JsonSerializerOptions options)
        {
            _bytes += value.Length;
            if (_attachments.Count >= _options.MaxAttachments || _bytes > _options.MaxPayloadBytes)
            {
                throw new InvalidOperationException("The outgoing binary packet exceeds its configured limits.");
            }

            writer.WriteStartObject();
            writer.WriteBoolean("_placeholder", true);
            writer.WriteNumber("num", _attachments.Count);
            writer.WriteEndObject();
            _attachments.Add([.. value]);
        }

        internal void Reset() => _bytes = 0;
    }
}
