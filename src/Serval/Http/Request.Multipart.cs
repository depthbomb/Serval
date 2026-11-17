#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Serval.Http.Constants;

namespace Serval.Http;

public sealed partial class Request
{
    /// <summary>Processes multipart sections sequentially without buffering the entire request or creating temporary files.</summary>
    /// <remarks>
    ///     Read each section's Body asynchronously inside the callback. Unread bytes are drained and validated before the next
    ///     callback. All form and request limits apply, including to skipped sections and epilogues. Choose this mode before
    ///     any other body reader. Failure consumes the request; application-owned destinations may contain partial data.
    /// </remarks>
    public async ValueTask ReadMultipartAsync(Func<MultipartSection, CancellationToken, ValueTask> readSection, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(readSection);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, RequestAborted);
        linked.Token.ThrowIfCancellationRequested();
        var type = FormDataParser.ContentType(Headers[HeaderNames.ContentType]);
        if (!string.Equals(type.MediaType, MimeTypes.MultipartFormData, StringComparison.OrdinalIgnoreCase))
        {
            throw new HttpErrorException(StatusCodes.UnsupportedMediaType, "A multipart form content type is required.");
        }

        FormOptions.Validate();
        await using var input = OpenBodyStreamCore(linked.Token, true);
        await FormDataParser.StreamMultipartAsync(input, type, FormOptions, readSection, linked.Token);
    }

    private async ValueTask<int> ReadBodyChunkAsync(Memory<byte> buffer, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();
        if (_bodyTooLarge)
        {
            throw new HttpErrorException(StatusCodes.PayloadTooLarge, "The request body is too large.");
        }

        var read = await _body.ReadAsync(buffer, ct);
        if (read > _maxBodyBytes - _bodyBytesRead)
        {
            _bodyTooLarge = true;
            throw new HttpErrorException(StatusCodes.PayloadTooLarge, "The request body is too large.");
        }

        _bodyBytesRead += read;
        return read;
    }

}
