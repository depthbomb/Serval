#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Serval.SocketIO;

/// <summary>JSON arguments and binary attachments from an event or acknowledgement.</summary>
public sealed class SocketIOArguments
{
    /// <summary>The number of application arguments.</summary>
    public int Count => _data.GetArrayLength() - _offset;

    /// <summary>Gets an argument's JSON representation. Binary values appear as protocol placeholders.</summary>
    public JsonElement this[int index] => index >= 0 && index < Count ? _data[index + _offset] : throw new ArgumentOutOfRangeException(nameof(index));

    private readonly JsonElement           _data;
    private readonly int                   _offset;
    private readonly JsonSerializerOptions _json;
    private readonly IReadOnlyList<byte[]> _attachments;

    private static readonly ConditionalWeakTable<JsonSerializerOptions, DecoderPool> Decoders = new();

    internal SocketIOArguments(JsonElement data, IReadOnlyList<byte[]> attachments, JsonSerializerOptions json, int offset = 0)
    {
        _data        = data;
        _offset      = offset;
        _json        = json;
        _attachments = attachments;
    }

    /// <summary>Deserializes an argument, restoring binary placeholders to byte arrays, including nested properties.</summary>
    public T? Get<T>(int index)
    {
        var data    = this[index];
        var pool    = Decoders.GetValue(_json, static value => new DecoderPool(value));
        var context = pool.Rent();
        try
        {
            context.Attachments.AddRange(_attachments);
            return data.Deserialize<T>(context.Json);
        }
        finally
        {
            pool.Return(context);
        }
    }

    private sealed class DecoderPool
    {
        private readonly Stack<DecoderContext> _available = new();
        private readonly JsonSerializerOptions _json;

        internal DecoderPool(JsonSerializerOptions json)
        {
            _json = json;
        }

        internal DecoderContext Rent()
        {
            lock (_available)
            {
                return _available.TryPop(out var context) ? context : new DecoderContext(_json);
            }
        }

        internal void Return(DecoderContext context)
        {
            context.Attachments.Clear();

            lock (_available)
            {
                if (_available.Count < Math.Max(2, Environment.ProcessorCount))
                {
                    _available.Push(context);
                }
            }
        }
    }

    private sealed class DecoderContext
    {
        internal List<byte[]>          Attachments { get; } = [];
        internal JsonSerializerOptions Json        { get; }

        internal DecoderContext(JsonSerializerOptions json)
        {
            Json = new JsonSerializerOptions(json);
            Json.Converters.Insert(0, new BinaryConverter(Attachments));
        }
    }

    private sealed class BinaryConverter : JsonConverter<byte[]>
    {
        private readonly IReadOnlyList<byte[]> _attachments;

        public BinaryConverter(IReadOnlyList<byte[]> attachments)
        {
            _attachments = attachments;
        }

        public override byte[] Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            var       value    = document.RootElement;
            if (value.ValueKind != JsonValueKind.Object               ||
                !value.TryGetProperty("_placeholder", out var marker) ||
                marker.ValueKind != JsonValueKind.True                ||
                !value.TryGetProperty("num", out var number)          ||
                number.ValueKind != JsonValueKind.Number              ||
                !number.TryGetInt32(out var index)                    || index < 0 || index >= _attachments.Count)
            {
                throw new JsonException("A binary attachment was expected.");
            }

            return [.. _attachments[index]];
        }

        public override void Write(Utf8JsonWriter writer, byte[] value, JsonSerializerOptions options) => throw new NotSupportedException();
    }
}
