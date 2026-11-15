#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Globalization;
using System.Text;
using Serval.Http;
using Serval.Http.Constants;
using Serval.Http.Protocol;

namespace Serval.Transport.Http2.Protocol;

internal sealed class HPack
{
    public const int MaximumTableSize = 4096;

    private int _capacity = MaximumTableSize;
    private int _tableBytes;

    private readonly List<(string Name, string Value, int Size)> _dynamicTable = [];

    // RFC 7541 Appendix A. Index zero is invalid on the wire.
    private static readonly (string Name, string Value)[] StaticHeaders =
    [
        ("", ""),
        (":authority", ""),
        (":method", HttpMethods.Get),
        (":method", HttpMethods.Post),
        (":path", "/"),
        (":path", "/index.html"),
        (":scheme", "http"),
        (":scheme", "https"),
        (":status", "200"),
        (":status", "204"),
        (":status", "206"),
        (":status", "304"),
        (":status", "400"),
        (":status", "404"),
        (":status", "500"),
        ("accept-charset", ""),
        ("accept-encoding", "gzip, deflate"),
        ("accept-language", ""),
        ("accept-ranges", ""),
        ("accept", ""),
        ("access-control-allow-origin", ""),
        ("age", ""),
        ("allow", ""),
        ("authorization", ""),
        ("cache-control", ""),
        ("content-disposition", ""),
        ("content-encoding", ""),
        ("content-language", ""),
        ("content-length", ""),
        ("content-location", ""),
        ("content-range", ""),
        ("content-type", ""),
        ("cookie", ""),
        ("date", ""),
        ("etag", ""),
        ("expect", ""),
        ("expires", ""),
        ("from", ""),
        ("host", ""),
        ("if-match", ""),
        ("if-modified-since", ""),
        ("if-none-match", ""),
        ("if-range", ""),
        ("if-unmodified-since", ""),
        ("last-modified", ""),
        ("link", ""),
        ("location", ""),
        ("max-forwards", ""),
        ("proxy-authenticate", ""),
        ("proxy-authorization", ""),
        ("range", ""),
        ("referer", ""),
        ("refresh", ""),
        ("retry-after", ""),
        ("server", ""),
        ("set-cookie", ""),
        ("strict-transport-security", ""),
        ("transfer-encoding", ""),
        ("user-agent", ""),
        ("vary", ""),
        ("via", ""),
        ("www-authenticate", ""),
    ];

    public List<(string Name, string Value)> Decode(ReadOnlySpan<byte> block, int maximumBytes)
    {
        List<(string Name, string Value)> fields    = [];
        var                               offset    = 0;
        long                              total     = 0;
        var                               fieldSeen = false;
        while (offset < block.Length)
        {
            var    first = block[offset];
            string name;
            string value;
            if ((first & 0x80) != 0)
            {
                (name, value) = Lookup(ReadInteger(block, ref offset, 7));
            }
            else if ((first & 0xe0) == 0x20)
            {
                var requested = ReadInteger(block, ref offset, 5);
                if (fieldSeen || requested > MaximumTableSize)
                {
                    throw Invalid("Invalid dynamic table size update.");
                }

                _capacity = requested;
                Evict();
                continue;
            }
            else
            {
                var indexed   = (first & 0x40) != 0;
                var nameIndex = ReadInteger(block, ref offset, indexed ? 6 : 4);
                name  = nameIndex == 0 ? ReadString(block, ref offset, maximumBytes) : Lookup(nameIndex).Name;
                value = ReadString(block, ref offset, maximumBytes);
                if (indexed)
                {
                    Add(name, value);
                }
            }

            fieldSeen =  true;
            // The protocol charges 32 bytes per entry as well as its octets, including indexed fields.
            total     += name.Length + value.Length + 32;
            if (total > maximumBytes)
            {
                throw new Http2ProtocolException(Http2Error.EnhanceYourCalm, "Decoded headers exceed the configured limit.");
            }

            fields.Add((name, value));
        }

        return fields;
    }

    public static byte[] Encode(int statusCode, Headers headers, long maximumBytes, int streamId)
    {
        // Responses never use a dynamic table. Declaring capacity zero also honors peer reductions.
        var bytes  = new List<byte> { 0x20 };
        var status = statusCode.ToString(CultureInfo.InvariantCulture);
        var total  = ":status".Length + status.Length + 32L;

        WriteLiteral(bytes, ":status", status);

        foreach (var header in headers)
        {
            var name = header.Key.ToLowerInvariant();
            if (name is "connection" or "transfer-encoding" or "keep-alive" or "upgrade" or "proxy-connection")
            {
                throw new InvalidOperationException($"Header '{header.Key}' is forbidden in HTTP/2.");
            }

            foreach (var value in header.Value)
            {
                total += name.Length + value.Length + 32L;
                if (total > maximumBytes)
                {
                    throw new Http2ProtocolException(Http2Error.EnhanceYourCalm, "Response headers exceed the peer's limit.", streamId);
                }

                WriteLiteral(bytes, name, value);
            }
        }

        return total > maximumBytes
            ? throw new Http2ProtocolException(Http2Error.EnhanceYourCalm, "Response headers exceed the peer's limit.", streamId)
            : [.. bytes];
    }

    private (string Name, string Value) Lookup(int index)
    {
        if (index > 0 && index < StaticHeaders.Length)
        {
            return StaticHeaders[index];
        }

        var dynamicIndex = index - StaticHeaders.Length;
        if (dynamicIndex < 0 || dynamicIndex >= _dynamicTable.Count)
        {
            throw Invalid("Invalid HPACK table index.");
        }

        var field = _dynamicTable[dynamicIndex];
        return (field.Name, field.Value);
    }

    private void Add(string name, string value)
    {
        var size = name.Length + value.Length + 32;
        if (size > _capacity)
        {
            _dynamicTable.Clear();
            _tableBytes = 0;
            return;
        }

        // Newest entries have the lowest dynamic index; evict from the opposite end.
        _dynamicTable.Insert(0, (name, value, size));
        _tableBytes += size;
        Evict();
    }

    private void Evict()
    {
        while (_tableBytes > _capacity)
        {
            _tableBytes -= _dynamicTable[^1].Size;
            _dynamicTable.RemoveAt(_dynamicTable.Count - 1);
        }
    }

    private static string ReadString(ReadOnlySpan<byte> bytes, ref int offset, int maximum)
    {
        if (offset >= bytes.Length)
        {
            throw Invalid("Missing HPACK string.");
        }

        var huffman = (bytes[offset] & 0x80) != 0;
        var length  = ReadInteger(bytes, ref offset, 7);
        if (length > bytes.Length - offset || length > maximum)
        {
            throw Invalid("Invalid HPACK string length.");
        }

        var encoded = bytes.Slice(offset, length);
        offset += length;
        try
        {
            return huffman ? Encoding.Latin1.GetString(HuffmanDecoder.Decode(encoded, maximum)) : Encoding.Latin1.GetString(encoded);
        }
        catch (InvalidDataException exception)
        {
            throw Invalid(exception.Message);
        }
    }

    private static int ReadInteger(ReadOnlySpan<byte> bytes, ref int offset, int prefix)
    {
        if (offset >= bytes.Length)
        {
            throw Invalid("Truncated HPACK integer.");
        }

        var  mask  = (1 << prefix) - 1;
        long value = bytes[offset++] & mask;
        if (value < mask)
        {
            return (int)value;
        }

        var shift = 0;
        while (true)
        {
            if (offset >= bytes.Length || shift > 28)
            {
                throw Invalid("Invalid HPACK integer.");
            }

            var next = bytes[offset++];
            value += (long)(next & 0x7f) << shift;
            if (value > int.MaxValue)
            {
                throw Invalid("HPACK integer overflow.");
            }

            if ((next & 0x80) == 0)
            {
                return (int)value;
            }

            shift += 7;
        }
    }

    private static void WriteLiteral(List<byte> bytes, string name, string value)
    {
        if (value.Any(character => character > 255))
        {
            throw new ArgumentException("HTTP/2 header values must contain Latin-1 characters.", nameof(value));
        }

        bytes.Add(0x10);
        WriteString(bytes, name);
        WriteString(bytes, value);
    }

    private static void WriteString(List<byte> bytes, string text)
    {
        var encoded = Encoding.Latin1.GetBytes(text);
        var length  = encoded.Length;
        bytes.Add((byte)Math.Min(length, 127));
        if (length >= 127)
        {
            length -= 127;
            while (length >= 128)
            {
                bytes.Add((byte)(length & 0x7f | 0x80));
                length >>= 7;
            }

            bytes.Add((byte)length);
        }

        bytes.AddRange(encoded);
    }

    private static Http2ProtocolException Invalid(string message) => new(Http2Error.Compression, message);
}
