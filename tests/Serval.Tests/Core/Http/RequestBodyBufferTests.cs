#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Net;
using System.Text;
using System.Text.Json;
using Serval.Http;
using Serval.Http.Constants;

namespace Serval.Tests.Core.Http;

public sealed class RequestBodyBufferTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(16384)]
    [InlineData(40000)]
    public async Task TextAndJsonReadOnlyTheWrittenBuffer(int length)
    {
        var       expected    = new string('x', length) + "こんにちは";
        using var textBody    = new MemoryStream(Encoding.UTF8.GetBytes(expected));
        var       textRequest = Request(textBody);
        Assert.Equal(expected, await textRequest.ReadTextAsync());

        using var jsonBody    = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(expected));
        var       jsonRequest = Request(jsonBody);
        Assert.Equal(expected, await jsonRequest.ReadJsonAsync<string>());
    }

    [Theory]
    [InlineData("bytes", 100000)]
    [InlineData("text", 12)]
    [InlineData("json", 12)]
    public async Task BodyReadsHonorLimitsAndCancellation(string format, int length)
    {
        using var body    = new MemoryStream(Encoding.UTF8.GetBytes("\"" + new string('x', length) + "\""));
        var       request = Request(body, 4);
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var error = await Assert.ThrowsAsync<HttpErrorException>(() => Read(request));
            Assert.Equal(StatusCodes.PayloadTooLarge, error.StatusCode);
        }

        using var cancelledBody = new MemoryStream("\"value\""u8.ToArray());
        using var cancellation  = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var cancelled = Request(cancelledBody);
        cancelled.RequestAborted = cancellation.Token;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Read(cancelled));
        Assert.Equal(0, cancelledBody.Position);

        Task Read(Request message)
        {
            return format switch
            {
                "bytes" => message.ReadBodyAsync().AsTask(),
                "text"  => message.ReadTextAsync().AsTask(),
                _       => message.ReadJsonAsync<string>().AsTask(),
            };
        }
    }

    private static Request Request(Stream body, long limit = 100000) => new("POST", new Uri("http://localhost/"), HttpVersion.Version11,
        new Headers { [HeaderNames.ContentType] = MimeTypes.Json }, body, limit);
}
