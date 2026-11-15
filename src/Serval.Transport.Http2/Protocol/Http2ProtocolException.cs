#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Serval.Hosting;

namespace Serval.Transport.Http2.Protocol;

internal sealed class Http2ProtocolException : RequestTransportException
{
    public uint ErrorCode { get; }
    public int  StreamId  { get; }

    public Http2ProtocolException(uint errorCode, string message, int streamId = 0) : base(message)
    {
        ErrorCode = errorCode;
        StreamId  = streamId;
    }
}

internal static class Http2Error
{
    public const uint NoError            = 0;
    public const uint Protocol           = 1;
    public const uint Internal           = 2;
    public const uint FlowControl        = 3;
    public const uint SettingsTimeout    = 4;
    public const uint StreamClosed       = 5;
    public const uint FrameSize          = 6;
    public const uint RefusedStream      = 7;
    public const uint Cancel             = 8;
    public const uint Compression        = 9;
    public const uint Connect            = 10;
    public const uint EnhanceYourCalm    = 11;
    public const uint InadequateSecurity = 12;
    public const uint Http11Required     = 13;
}
