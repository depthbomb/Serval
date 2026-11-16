#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Globalization;
using System.Text;
using Serval.Http;

namespace Serval.Transport.Http3.Protocol;

internal static class QPack
{
    // A zero-capacity dynamic table is valid QPACK and avoids blocked request streams.
    public static List<(string Name, string Value)> Decode(ReadOnlySpan<byte> payload, int maximum)
    {
        var offset = 0;
        if (ReadInteger(payload, ref offset, 8) != 0 || ReadInteger(payload, ref offset, 7) != 0 || (payload[1] & 0x80) != 0)
        {
            throw Invalid();
        }

        List<(string Name, string Value)> result = [];
        long                              total  = 0;
        while (offset < payload.Length)
        {
            var    first = payload[offset];
            string name;
            string value;
            if ((first & 0x80) != 0)
            {
                if ((first & 0x40) == 0)
                {
                    throw Invalid();
                }

                (name, value) = Static(ReadInteger(payload, ref offset, 6));
            }
            else if ((first & 0x40) != 0)
            {
                if ((first & 0x10) == 0)
                {
                    throw Invalid();
                }

                name  = Static(ReadInteger(payload, ref offset, 4)).Name;
                value = ReadString(payload, ref offset, 7, 0x80, maximum);
            }
            else if ((first & 0x20) != 0)
            {
                name  = ReadString(payload, ref offset, 3, 0x08, maximum);
                value = ReadString(payload, ref offset, 7, 0x80, maximum);
            }
            else
            {
                throw Invalid();
            }

            total += name.Length + value.Length + 32;
            if (total > maximum)
            {
                throw new Http3ProtocolException(Http3Error.ExcessiveLoad, "Decoded headers exceed the configured limit.");
            }

            result.Add((name, value));
        }

        return result;
    }

    public static byte[] Encode(int statusCode, Headers headers)
    {
        var bytes = new List<byte>
        {
            0,
            0
        };
        WriteLiteral(bytes, ":status", statusCode.ToString(CultureInfo.InvariantCulture));

        foreach (var header in headers)
        {
            var name = header.Key.ToLowerInvariant();
            if (name is "connection" or "transfer-encoding" or "keep-alive" or "upgrade" or "proxy-connection")
            {
                throw new InvalidOperationException($"Header '{header.Key}' is forbidden in HTTP/3.");
            }

            foreach (var value in header.Value)
            {
                WriteLiteral(bytes, name, value);
            }
        }

        return [.. bytes];
    }

    private static string ReadString(ReadOnlySpan<byte> bytes, ref int offset, int prefix, byte huffmanFlag, int maximum)
    {
        if (offset >= bytes.Length)
        {
            throw Invalid();
        }

        var huffman = (bytes[offset] & huffmanFlag) != 0;
        var length  = ReadInteger(bytes, ref offset, prefix);
        if (length > bytes.Length - offset || length > maximum)
        {
            throw Invalid();
        }

        var encoded = bytes.Slice(offset, length);
        offset += length;
        // Field values are octets. UTF-8 replacement decoding can collapse distinct wire values.
        return huffman ? Huffman.Decode(encoded, maximum) : Encoding.Latin1.GetString(encoded);
    }

    private static int ReadInteger(ReadOnlySpan<byte> bytes, ref int offset, int prefix)
    {
        if (offset >= bytes.Length)
        {
            throw Invalid();
        }

        var mask  = (1 << prefix) - 1;
        var value = bytes[offset++] & mask;
        if (value < mask)
        {
            return value;
        }

        var shift = 0;
        while (true)
        {
            if (offset >= bytes.Length || shift > 21)
            {
                throw Invalid();
            }

            var next = bytes[offset++];
            value = checked(value + ((next & 0x7f) << shift));
            if ((next & 0x80) == 0)
            {
                return value;
            }

            shift += 7;
        }
    }

    private static void WriteLiteral(List<byte> bytes, string name, string value)
    {
        if (value.AsSpan().ContainsAnyInRange('\u0100', '\uffff'))
        {
            throw new ArgumentException("HTTP/3 header values must contain Latin-1 characters.", nameof(value));
        }

        var encodedName  = Encoding.Latin1.GetBytes(name);
        var encodedValue = Encoding.Latin1.GetBytes(value);
        WriteInteger(bytes, encodedName.Length, 3, 0x30);

        bytes.AddRange(encodedName);

        WriteInteger(bytes, encodedValue.Length, 7, 0);

        bytes.AddRange(encodedValue);
    }

    private static void WriteInteger(List<byte> bytes, int value, int prefix, byte flags)
    {
        var mask = (1 << prefix) - 1;
        bytes.Add((byte)(flags | Math.Min(value, mask)));
        if (value < mask)
        {
            return;
        }

        value -= mask;
        while (value >= 128)
        {
            bytes.Add((byte)(value & 0x7f | 0x80));
            value >>= 7;
        }

        bytes.Add((byte)value);
    }

    private static (string Name, string Value) Static(int index) => index < ProtocolTables.StaticHeaders.Length ? ProtocolTables.StaticHeaders[index] : throw Invalid();

    private static Http3ProtocolException Invalid() => new(Http3Error.QpackDecompression, "Invalid QPACK field section.");
}
