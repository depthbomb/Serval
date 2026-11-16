#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Serval.Savanna.LanguageServer.Rpc;

public sealed class Protocol
{
    private const int MaximumMessageBytes = 16 * 1024 * 1024;

    private int _readCount;
    private int _readOffset;

    private readonly Stream        _input;
    private readonly Stream        _output;
    private readonly byte[]        _readBuffer = new byte[8192];
    private readonly SemaphoreSlim _writeLock  = new(1);

    public Protocol(Stream input, Stream output)
    {
        _input  = input;
        _output = output;
    }

    public async Task<JsonDocument?> ReadAsync(CancellationToken cancellationToken)
    {
        var scanned = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var headerEnd = _readBuffer.AsSpan(_readOffset + scanned, _readCount - _readOffset - scanned).IndexOf("\r\n\r\n"u8);
            if (headerEnd >= 0)
            {
                headerEnd += scanned;
                var lengths = Encoding.ASCII.GetString(_readBuffer, _readOffset, headerEnd).Split("\r\n")
                                      .Where(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)).ToArray();
                if (lengths.Length != 1                || !int.TryParse(lengths[0].AsSpan(15).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var length)
                                                       || length <= 0 || length > MaximumMessageBytes)
                {
                    throw new InvalidDataException("Invalid LSP Content-Length.");
                }

                _readOffset += headerEnd + 4;
                var body     = new byte[length];
                var buffered = Math.Min(length, _readCount - _readOffset);
                _readBuffer.AsSpan(_readOffset, buffered).CopyTo(body);
                _readOffset += buffered;
                await _input.ReadExactlyAsync(body.AsMemory(buffered), cancellationToken);
                return JsonDocument.Parse(body);
            }

            scanned = Math.Max(0, _readCount - _readOffset - 3);
            if (_readOffset > 0)
            {
                _readBuffer.AsSpan(_readOffset, _readCount - _readOffset).CopyTo(_readBuffer);
                _readCount  -= _readOffset;
                _readOffset =  0;
            }

            if (_readCount == _readBuffer.Length)
            {
                throw new InvalidDataException("LSP header is too large.");
            }

            var read = await _input.ReadAsync(_readBuffer.AsMemory(_readCount), cancellationToken);
            if (read == 0)
            {
                if (_readCount == 0)
                {
                    return null;
                }

                throw new EndOfStreamException("Incomplete LSP header.");
            }

            _readCount += read;
        }
    }

    public async Task WriteAsync(object message, CancellationToken cancellationToken = default)
    {
        var body   = JsonSerializer.SerializeToUtf8Bytes(message);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await _output.WriteAsync(header, cancellationToken);
            await _output.WriteAsync(body, cancellationToken);
            await _output.FlushAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
