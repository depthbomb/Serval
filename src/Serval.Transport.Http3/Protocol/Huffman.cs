#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Text;
using Serval.Http.Protocol;

namespace Serval.Transport.Http3.Protocol;

internal static class Huffman
{
    public static string Decode(ReadOnlySpan<byte> encoded, int maximum)
    {
        try
        {
            return Encoding.Latin1.GetString(HuffmanDecoder.Decode(encoded, maximum));
        }
        catch (InvalidDataException exception)
        {
            throw new Http3ProtocolException(Http3Error.QpackDecompression, exception.Message);
        }
    }
}
