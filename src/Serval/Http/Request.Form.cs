#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Buffers;
using System.Net.Http.Headers;
using System.Text.Json.Serialization.Metadata;
using Serval.Http.Constants;

namespace Serval.Http;

public sealed partial class Request
{
    /// <summary>Whether Content-Type identifies a URL-encoded or multipart form.</summary>
    public bool HasFormContentType => MediaTypeHeaderValue.TryParse(Headers[HeaderNames.ContentType], out var type) && FormDataParser.IsForm(type);

    internal FormOptions              FormOptions { get; set; } = new();
    private  CancellationTokenSource? _dataCancellation;

    private          Task<RequestData>?  _dataTask;
    private          bool                _disposed;
    private          bool                _rawBodyRead;
    private          bool                _readingBody;
    private          bool                _streamedMultipart;
    private          RequestBodyStorage? _storage;
    private readonly Lock                _bodyGate = new();

    /// <summary>Releases parsed JSON and temporary upload files. The transport retains ownership of the incoming stream.</summary>
    /// <remarks>The framework calls this after request processing. Dispose manually constructed requests after use.</remarks>
    public async ValueTask DisposeAsync()
    {
        Task<RequestData>? task;
        lock (_bodyGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            task      = _dataTask;
        }

        try
        {
            if (_dataCancellation is not null)
            {
                await _dataCancellation.CancelAsync();
            }
        }
        finally
        {
            try
            {
                if (task is not null)
                {
                    try
                    {
                        (await task).Dispose();
                    }
                    catch (Exception) when (task.IsFaulted || task.IsCanceled) {}
                }
            }
            finally
            {
                try
                {
                    if (_storage is not null)
                    {
                        await _storage.DisposeAsync();
                    }
                }
                finally
                {
                    _dataCancellation?.Dispose();
                }
            }
        }
    }

    /// <summary>Parses and caches URL-encoded or multipart form fields and files.</summary>
    /// <remarks>
    ///     UTF-8 is supported. Cannot be combined with raw body or whole-body JSON reads. Files live until request
    ///     disposal.
    /// </remarks>
    public ValueTask<RequestData> ReadFormAsync(CancellationToken ct = default)
    {
        if (!FormDataParser.IsForm(FormDataParser.ContentType(Headers[HeaderNames.ContentType])))
        {
            throw new HttpErrorException(StatusCodes.UnsupportedMediaType, "A form content type is required.");
        }

        return ReadPostAsync(ct);
    }

    /// <summary>Parses and caches form data or top-level JSON properties, regardless of the request's HTTP method.</summary>
    /// <remarks>
    ///     Repeated calls share one parse. Names ignore case; duplicate JSON names are rejected. Required scalar reads reject
    ///     nulls and repeated fields.
    ///     A failed or canceled parse remains failed. Raw body and ReadJsonAsync reads cannot be mixed with these cached
    ///     readers.
    /// </remarks>
    public ValueTask<RequestData> ReadPostAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        RequestAborted.ThrowIfCancellationRequested();
        lock (_bodyGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_dataTask is null)
            {
                if (_readingBody || _rawBodyRead)
                {
                    throw new InvalidOperationException("Read form or POST data before consuming the raw request body.");
                }

                var contentType = FormDataParser.ContentType(Headers[HeaderNames.ContentType]);
                if (!FormDataParser.IsForm(contentType) && !string.Equals(contentType.MediaType, MimeTypes.Json, StringComparison.OrdinalIgnoreCase)
                                                        && !(contentType.MediaType?.StartsWith("application/", StringComparison.OrdinalIgnoreCase) == true && contentType.MediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase)))
                {
                    throw new HttpErrorException(StatusCodes.UnsupportedMediaType, "A form or JSON content type is required.");
                }

                FormOptions.Validate();
                _dataCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, RequestAborted);
                _dataTask         = ReadDataCoreAsync(contentType, _dataCancellation.Token);
            }

            return new ValueTask<RequestData>(_dataTask.WaitAsync(ct));
        }
    }

    /// <summary>Reads one required POST scalar from a form or JSON body.</summary>
    public async ValueTask<T> PostValueAsync<T>(string name, CancellationToken ct = default) where T : IParsable<T>
        => (await ReadPostAsync(ct)).Get<T>(name);

    /// <summary>Reads a POST scalar, using a fallback only when absent.</summary>
    public async ValueTask<T> PostValueAsync<T>(string name, T fallback, CancellationToken ct = default) where T : IParsable<T>
        => (await ReadPostAsync(ct)).Get(name, fallback);

    /// <summary>Reads a required top-level JSON value using explicit metadata, including objects and arrays.</summary>
    public async ValueTask<T> PostValueAsync<T>(string name, JsonTypeInfo<T> typeInfo, CancellationToken ct = default)
        => (await ReadPostAsync(ct)).Get(name, typeInfo);

    /// <summary>Reads repeated POST fields or a JSON array as scalars. Missing values return an empty array.</summary>
    public async ValueTask<T[]> PostValuesAsync<T>(string name, CancellationToken ct = default) where T : IParsable<T>
        => (await ReadPostAsync(ct)).GetValues<T>(name);

    /// <summary>Streams the remaining raw request body to a destination, enforcing cumulative size limits and cancellation.</summary>
    /// <remarks>Does not close the destination or buffer the full body. Cannot be combined with cached form or POST readers.</remarks>
    public async ValueTask CopyBodyToAsync(Stream destination, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ct.ThrowIfCancellationRequested();
        RequestAborted.ThrowIfCancellationRequested();
        lock (_bodyGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_dataTask is not null || _readingBody || _streamedMultipart)
            {
                throw new InvalidOperationException("The body is already being read or has been reserved for parsed POST data.");
            }

            _readingBody = true;
            _rawBodyRead = true;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, RequestAborted);
        try
        {
            await ReadChunksAsync(destination.WriteAsync, linked.Token);
        }
        finally
        {
            lock (_bodyGate)
            {
                _readingBody = false;
            }
        }
    }

    internal IReadOnlyList<string> ParsedFormValues(string name)
        => _dataTask is { IsCompletedSuccessfully: true } ? _dataTask.Result.FormValues(name) : throw new InvalidOperationException("Read the form before binding its fields.");

    internal void ApplyBodyPolicy(long maxBytes, FormOptions forms)
    {
        lock (_bodyGate)
        {
            if (_rawBodyRead || _readingBody || _dataTask is not null)
            {
                if (maxBytes < _maxBodyBytes || !FormOptions.Matches(forms))
                {
                    throw new InvalidOperationException("Read request bodies after endpoint selection so endpoint limits can be applied.");
                }

                return;
            }

            _maxBodyBytes = Math.Min(_maxBodyBytes, maxBytes);
            FormOptions   = forms;
            if (Headers[HeaderNames.ContentLength] is {} length && long.TryParse(length, out var declared) && declared > _maxBodyBytes)
            {
                throw new HttpErrorException(StatusCodes.PayloadTooLarge, "The request body is too large.");
            }
        }
    }

    private async Task<RequestData> ReadDataCoreAsync(MediaTypeHeaderValue type, CancellationToken ct)
    {
        _storage = new RequestBodyStorage(FormOptions);
        try
        {
            await ReadChunksAsync(_storage.AppendAsync, ct);
            var data = await FormDataParser.ParseAsync(_storage, type, FormOptions, JsonOptions, ct);
            if (data.Files.Count == 0)
            {
                await _storage.DisposeAsync();
            }

            return data;
        }
        catch
        {
            await _storage.DisposeAsync();
            throw;
        }
    }

    private async ValueTask ReadChunksAsync(Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> consume, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_bodyTooLarge)
        {
            throw new HttpErrorException(StatusCodes.PayloadTooLarge, "The request body is too large.");
        }

        var buffer = ArrayPool<byte>.Shared.Rent(16384);
        try
        {
            while (true)
            {
                var read = await ReadBodyChunkAsync(buffer.AsMemory(), ct);
                if (read == 0)
                {
                    break;
                }

                await consume(buffer.AsMemory(0, read), ct);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, true);
        }
    }
}
