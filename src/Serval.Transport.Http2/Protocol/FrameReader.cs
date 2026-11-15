#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Buffers.Binary;

namespace Serval.Transport.Http2.Protocol;

internal sealed class FrameReader
{
    private          int    _end;
    private          int    _start;
    private readonly byte[] _buffer = new byte[32 * 1024];
    private readonly Stream _stream;

    public FrameReader(Stream stream)
    {
        _stream = stream;
    }

    // The payload borrows this reader's buffer until the next ReadAsync call.
    // Frame processing must consume it or copy anything it keeps for later.
    public async ValueTask<Frame?> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!await EnsureAsync(9, cancellationToken))
        {
            return null;
        }

        var length = _buffer[_start] << 16 | _buffer[_start + 1] << 8 | _buffer[_start + 2];
        if (length > Frames.MaximumPayload)
        {
            throw new Http2ProtocolException(Http2Error.FrameSize, "The frame exceeds the advertised maximum size.");
        }

        await EnsureAsync(9 + length, cancellationToken);

        var frame = new Frame(_buffer[_start + 3], _buffer[_start + 4], BinaryPrimitives.ReadInt32BigEndian(_buffer.AsSpan(_start + 5)) & int.MaxValue, _buffer.AsMemory(_start + 9, length));

        _start += 9 + length;

        return frame;
    }

    private async ValueTask<bool> EnsureAsync(int count, CancellationToken cancellationToken)
    {
        if (_end - _start >= count)
        {
            return true;
        }

        _buffer.AsSpan(_start, _end - _start).CopyTo(_buffer);
        _end   -= _start;
        _start =  0;
        do
        {
            var read = await _stream.ReadAsync(_buffer.AsMemory(_end), cancellationToken);
            if (read == 0)
            {
                return _end == 0 ? false : throw new EndOfStreamException("The HTTP/2 frame is truncated.");
            }

            _end += read;
        } while (_end < count);

        return true;
    }
}
