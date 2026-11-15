#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Buffers.Binary;

namespace Serval.Transport.Http2.Protocol;

internal readonly record struct Frame(byte Type, byte Flags, int StreamId, ReadOnlyMemory<byte> Payload);

internal static class Frames
{
    public const byte Data           = 0;
    public const byte Headers        = 1;
    public const byte Priority       = 2;
    public const byte ResetStream    = 3;
    public const byte Settings       = 4;
    public const byte PushPromise    = 5;
    public const byte Ping           = 6;
    public const byte GoAway         = 7;
    public const byte WindowUpdate   = 8;
    public const byte Continuation   = 9;
    public const byte EndStream      = 1;
    public const byte Ack            = 1;
    public const byte EndHeaders     = 4;
    public const byte Padded         = 8;
    public const byte HasPriority    = 32;
    public const int  MaximumPayload = 16384;
    public const int  InitialWindow  = 65535;

    public static async ValueTask<Frame?> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[9];
        if (await stream.ReadAsync(header.AsMemory(0, 1), cancellationToken) == 0)
        {
            return null;
        }

        await stream.ReadExactlyAsync(header.AsMemory(1), cancellationToken);
        var length = header[0] << 16 | header[1] << 8 | header[2];
        if (length > MaximumPayload)
        {
            throw new Http2ProtocolException(Http2Error.FrameSize, "The frame exceeds the advertised maximum size.");
        }

        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken);
        return new Frame(header[3], header[4], BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(5)) & int.MaxValue, payload);
    }

    public static byte[] Header(byte type, byte flags, int streamId, int length)
    {
        var bytes = new byte[9];
        WriteHeader(bytes, type, flags, streamId, length);
        return bytes;
    }

    public static void WriteHeader(Span<byte> bytes, byte type, byte flags, int streamId, int length)
    {
        bytes[0] = (byte)(length >> 16);
        bytes[1] = (byte)(length >> 8);
        bytes[2] = (byte)length;
        bytes[3] = type;
        bytes[4] = flags;
        BinaryPrimitives.WriteInt32BigEndian(bytes[5..], streamId);
    }

    public static void RequireLength(Frame frame, int length)
    {
        if (frame.Payload.Length != length)
        {
            throw new Http2ProtocolException(Http2Error.FrameSize, "Invalid frame payload length.");
        }
    }

    public static void RequireConnection(Frame frame)
    {
        if (frame.StreamId != 0)
        {
            throw new Http2ProtocolException(Http2Error.Protocol, "This frame must use stream zero.");
        }
    }

    public static void RequireStream(Frame frame)
    {
        if (frame.StreamId == 0)
        {
            throw new Http2ProtocolException(Http2Error.Protocol, "This frame requires a request stream.");
        }
    }
}
