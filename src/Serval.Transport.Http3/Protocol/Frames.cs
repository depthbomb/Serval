#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Buffers;

namespace Serval.Transport.Http3.Protocol;

internal static class Frames
{
    public const long Data     = 0;
    public const long Headers  = 1;
    public const long Settings = 4;
    public const long GoAway   = 7;

    public static async ValueTask<long?> ReadIntegerAsync(Stream stream, CancellationToken cancellationToken)
    {
        var bytes = new byte[8];
        var read  = await stream.ReadAsync(bytes.AsMemory(0, 1), cancellationToken);
        if (read == 0)
        {
            return null;
        }

        // The top two bits select 1, 2, 4, or 8 bytes; the remaining bits are the integer payload.
        var length = 1 << (bytes[0] >> 6);
        bytes[0] &= 0x3f;
        try
        {
            await stream.ReadExactlyAsync(bytes.AsMemory(1, length - 1), cancellationToken);
        }
        catch (EndOfStreamException)
        {
            throw new Http3ProtocolException(Http3Error.Frame, "Truncated QUIC integer.");
        }

        long value = 0;
        for (var index = 0; index < length; index++)
        {
            value = value << 8 | bytes[index];
        }

        return value;
    }

    public static byte[] Integer(long value)
    {
        if (value is < 0 or >= 1L << 62)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        var length    = value < 64 ? 1 : value < 16384 ? 2 : value < 1073741824 ? 4 : 8;
        var bytes     = new byte[length];
        var remaining = value;
        for (var index = length - 1; index >= 0; index--)
        {
            bytes[index] =   (byte)remaining;
            remaining    >>= 8;
        }

        bytes[0] |= (byte)(length switch { 1 => 0, 2 => 1, 4 => 2, _ => 3 } << 6);
        return bytes;
    }

    public static async ValueTask<(long Type, long Length)?> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var type = await ReadIntegerAsync(stream, cancellationToken);
        if (type is null)
        {
            return null;
        }

        var length = await ReadIntegerAsync(stream, cancellationToken) ?? throw new Http3ProtocolException(Http3Error.Frame, "Truncated frame.");
        return (type.Value, length);
    }

    public static async ValueTask WriteAsync(Stream stream, long type, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        var prefix = Integer(type).Concat(Integer(payload.Length)).ToArray();
        await stream.WriteAsync(prefix, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
    }

    public static async ValueTask SkipAsync(Stream stream, long length, CancellationToken cancellationToken)
    {
        // Extension frames can be large. Skipping must use bounded memory rather than allocate their declared length.
        var buffer = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            while (length > 0)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(length, buffer.Length)), cancellationToken);
                if (read == 0)
                {
                    throw new Http3ProtocolException(Http3Error.Frame, "Truncated frame payload.");
                }

                length -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public static async ValueTask<byte[]> ReadPayloadAsync(Stream stream, long length, int maximum, CancellationToken cancellationToken)
    {
        if (length > maximum)
        {
            throw new Http3ProtocolException(Http3Error.ExcessiveLoad, "Frame payload exceeds the configured limit.");
        }

        var bytes = new byte[(int)length];
        try
        {
            await stream.ReadExactlyAsync(bytes, cancellationToken);
        }
        catch (EndOfStreamException)
        {
            throw new Http3ProtocolException(Http3Error.Frame, "Truncated frame payload.");
        }

        return bytes;
    }
}
