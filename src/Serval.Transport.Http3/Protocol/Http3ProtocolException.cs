#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Serval.Hosting;

namespace Serval.Transport.Http3.Protocol;

internal sealed class Http3ProtocolException : RequestTransportException
{
    public long ErrorCode { get; }

    public Http3ProtocolException(long errorCode, string message) : base(message)
    {
        ErrorCode = errorCode;
    }
}

internal static class Http3Error
{
    public const long NoError              = 0x100;
    public const long GeneralProtocol      = 0x101;
    public const long StreamCreation       = 0x103;
    public const long ClosedCriticalStream = 0x104;
    public const long FrameUnexpected      = 0x105;
    public const long Frame                = 0x106;
    public const long ExcessiveLoad        = 0x107;
    public const long Settings             = 0x109;
    public const long MissingSettings      = 0x10a;
    public const long RequestCancelled     = 0x10c;
    public const long Message              = 0x10e;
    public const long QpackDecompression   = 0x200;
    public const long QpackEncoder         = 0x201;
    public const long QpackDecoder         = 0x202;
}
