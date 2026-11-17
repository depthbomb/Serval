#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Serval.Http;
using Serval.Http.Constants;
using Serval.Tests.Support;

namespace Serval.Tests.Core.Http;

public sealed class EarlyHintsTests
{
    [Fact]
    public async Task HintsOwnTheirHeadersAndLeaveTheFinalResponseWritable()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new HintResponse
        {
            Send = (_, ct) => new ValueTask(release.Task.WaitAsync(ct))
        };
        response.StatusCode         = StatusCodes.Created;
        response.Headers["X-Final"] = "before";
        response.Cookies.Set("session", "private");
        var hints = new Headers();
        hints.Append(HeaderNames.Link, "</one.css>; rel=preload; as=style");
        hints.Append(HeaderNames.Link, "</two.js>; rel=modulepreload");
        var pending = response.SendEarlyHintsAsync(hints).AsTask();
        Assert.False(pending.IsCompleted);
        hints[HeaderNames.Link] = "</later.css>; rel=preload; as=style";
        release.SetResult();
        Assert.True(await pending);

        var first = Assert.Single(response.Sent);
        Assert.Equal(["</one.css>; rel=preload; as=style", "</two.js>; rel=modulepreload"], first.GetValues(HeaderNames.Link));
        Assert.Null(first[HeaderNames.SetCookie]);
        Assert.Null(first["X-Final"]);
        Assert.Throws<InvalidOperationException>(() => first[HeaderNames.Link] = "changed");
        Assert.False(response.HasStarted);
        Assert.Equal(StatusCodes.Created, response.StatusCode);
        Assert.Null(response.Headers[HeaderNames.Link]);
        Assert.True(await response.SendEarlyHintsAsync(hints));
        Assert.Equal(2, response.Sent.Count);
        Assert.Equal(hints[HeaderNames.Link], response.Sent[1][HeaderNames.Link]);

        response.StatusCode         = StatusCodes.Accepted;
        response.Headers["X-Final"] = "after";
        response.Cookies.Set("another", "value");
        await response.CompleteAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => response.SendEarlyHintsAsync(hints).AsTask());
    }

    [Fact]
    public async Task UnsupportedTransportsLeaveTheFinalResponseUntouched()
    {
        var response = new MemoryResponse
        {
            StatusCode = StatusCodes.Created
        };
        var hints = new Headers
        {
            [HeaderNames.Link] = "</site.css>; rel=preload; as=style"
        };
        Assert.False(response.SupportsEarlyHints);
        Assert.False(await response.SendEarlyHintsAsync(hints));
        Assert.False(response.HasStarted);
        Assert.Empty(response.Headers);
        hints.Clear();
        await response.WriteTextAsync("final");
        Assert.Equal(StatusCodes.Created, response.StatusCode);
        Assert.Equal("final", response.Text);
    }

    [Theory]
    [InlineData("content-length")]
    [InlineData("Transfer-Encoding")]
    [InlineData("CONNECTION")]
    [InlineData("Upgrade")]
    [InlineData("Keep-Alive")]
    [InlineData("Proxy-Connection")]
    [InlineData("TE")]
    [InlineData("Trailer")]
    public async Task HintsRejectFramingAndConnectionFieldsBeforeWriting(string name)
    {
        var response = new HintResponse();
        await Assert.ThrowsAsync<ArgumentException>(() => response.SendEarlyHintsAsync(new Headers
        {
            [name] = "0"
        }).AsTask());
        Assert.Empty(response.Sent);
        Assert.False(response.HasStarted);
        await response.CompleteAsync();
    }

    [Fact]
    public async Task InvalidArgumentsAndPreCanceledSendsDoNotCommitHeaders()
    {
        var response = new HintResponse();
        await Assert.ThrowsAsync<ArgumentNullException>(() => response.SendEarlyHintsAsync(null!).AsTask());
        using var ct = new CancellationTokenSource();
        ct.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => response.SendEarlyHintsAsync(new Headers(), ct.Token).AsTask());
        Assert.Empty(response.Sent);
        Assert.False(response.HasStarted);
        await response.CompleteAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrCanceledHeaderWritesPreventAReplacementResponse(bool cancel)
    {
        Exception failure = cancel ? new OperationCanceledException() : new IOException("partial header write");
        var response = new HintResponse
        {
            Send = (_, _) => ValueTask.FromException(failure)
        };
        Assert.Same(failure, await Record.ExceptionAsync(() => response.SendEarlyHintsAsync(new Headers()).AsTask()));
        Assert.True(response.HasStarted);
        Assert.Throws<InvalidOperationException>(() => response.Headers["X-Final"] = "unsafe");
        Assert.Throws<InvalidOperationException>(() => response.StatusCode         = StatusCodes.InternalServerError);
        await Assert.ThrowsAsync<InvalidOperationException>(() => response.WriteTextAsync("unsafe").AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => response.CompleteAsync().AsTask());
    }

    private sealed class HintResponse : Response
    {
        public override bool                                        SupportsEarlyHints => true;
        public          List<Headers>                               Sent               { get; }       = [];
        public          Func<Headers, CancellationToken, ValueTask> Send               { get; init; } = (_, _) => ValueTask.CompletedTask;

        public override ValueTask FlushAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public override ValueTask CompleteAsync(CancellationToken ct = default) => StartAsync(ct);

        public override ValueTask AbortAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        protected override ValueTask OnStartAsync(CancellationToken ct) => ValueTask.CompletedTask;

        protected override ValueTask OnWriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct) => ValueTask.CompletedTask;

        protected override async ValueTask OnSendEarlyHintsAsync(Headers headers, CancellationToken ct)
        {
            await Send(headers, ct);
            Sent.Add(headers);
        }
    }
}
