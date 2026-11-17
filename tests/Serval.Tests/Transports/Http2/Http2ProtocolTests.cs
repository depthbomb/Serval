#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Serval.Http;
using Serval.Http.Constants;
using Serval.Transport.Http2.Protocol;

namespace Serval.Tests.Transports.Http2;

public sealed class Http2ProtocolTests
{
    [Fact]
    public void HpackDecodesRfcRequestsWithSharedDynamicState()
    {
        var decoder = new HPack();
        var first   = decoder.Decode(Convert.FromHexString("828684418cf1e3c2e5f23a6ba0ab90f4ff"), 4096);
        Assert.Contains((":authority", "www.example.com"), first);
        var second = decoder.Decode(Convert.FromHexString("828684be5886a8eb10649cbf"), 4096);
        Assert.Contains((":authority", "www.example.com"), second);
        Assert.Contains(("cache-control", "no-cache"), second);
        var third = decoder.Decode(Convert.FromHexString("828785bf408825a849e95ba97d7f8925a849e95bb8e8b4bf"), 4096);
        Assert.Contains((":scheme", "https"), third);
        Assert.Contains(("custom-key", "custom-value"), third);
    }

    [Theory]
    [InlineData("80")]
    [InlineData("40")]
    [InlineData("ff")]
    [InlineData("8220")]
    [InlineData("3fe21f")]
    [InlineData("40017881ff")]
    [InlineData("be")]
    public void InvalidHpackIsRejected(string block)
    {
        var exception = Assert.Throws<Http2ProtocolException>(() => new HPack().Decode(Convert.FromHexString(block), 4096));
        Assert.Equal(Http2Error.Compression, exception.ErrorCode);
    }

    [Fact]
    public void HpackEvictsEntriesAndPreservesSeparateSetCookieFields()
    {
        var decoder = new HPack();
        decoder.Decode(Convert.FromHexString("204001780179"), 4096);
        Assert.Throws<Http2ProtocolException>(() => decoder.Decode(Convert.FromHexString("be"), 4096));
        var headers = new Headers();
        headers.Append(HeaderNames.SetCookie, "one=1");
        headers.Append(HeaderNames.SetCookie, "two=2");
        var fields = new HPack().Decode(HPack.Encode(StatusCodes.Ok, headers, 4096, 1), 4096);
        Assert.Contains(("set-cookie", "one=1"), fields);
        Assert.Contains(("set-cookie", "two=2"), fields);
    }

    [Fact]
    public async Task SendingRequiresBothConnectionAndStreamCredit()
    {
        var flow = new SendFlowControl();
        flow.Open(1);
        Assert.Equal(65535, await flow.ReserveAsync(1, 65535, CancellationToken.None));
        var pending = flow.ReserveAsync(1, 10, CancellationToken.None).AsTask();
        Assert.False(pending.IsCompleted);
        flow.Update(0, 10);
        Assert.False(pending.IsCompleted);
        flow.Update(1, 10);
        Assert.Equal(10, await pending);
    }

    [Fact]
    public async Task InitialWindowReductionsCanMakeAStreamWindowNegative()
    {
        var flow = new SendFlowControl();
        flow.Open(1);
        await flow.ReserveAsync(1, 100, CancellationToken.None);
        flow.UpdateInitialWindow(0);
        var pending = flow.ReserveAsync(1, 10, CancellationToken.None).AsTask();
        flow.Update(1, 99);
        Assert.False(pending.IsCompleted);
        flow.Update(1, 2);
        Assert.Equal(1, await pending);
    }

    [Fact]
    public async Task CancelledFlowControlWaitsDoNotBlockOtherStreams()
    {
        var flow = new SendFlowControl();
        flow.Open(1);
        flow.Open(3);
        flow.UpdateInitialWindow(0);
        using var cancellation = new CancellationTokenSource();
        var       pending      = flow.ReserveAsync(1, 10, cancellation.Token).AsTask();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        flow.Update(3, 1);
        Assert.Equal(1, await flow.ReserveAsync(3, 1, CancellationToken.None));
    }

    [Fact]
    public void WindowOverflowAndZeroIncrementsAreProtocolErrors()
    {
        var flow = new SendFlowControl();
        Assert.Equal(Http2Error.Protocol, Assert.Throws<Http2ProtocolException>(() => flow.Update(0, 0)).ErrorCode);
        Assert.Equal(Http2Error.FlowControl, Assert.Throws<Http2ProtocolException>(() => flow.Update(0, int.MaxValue)).ErrorCode);
    }

    [Fact]
    public async Task RemovingAStreamCancelsItsWaitWithoutCompletingAnotherStream()
    {
        var flow = new SendFlowControl();
        flow.UpdateInitialWindow(0);
        flow.Open(1);
        flow.Open(3);
        var removed   = flow.ReserveAsync(1, 10, CancellationToken.None).AsTask();
        var remaining = flow.ReserveAsync(3, 10, CancellationToken.None).AsTask();
        flow.Remove(1);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => removed.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.False(remaining.IsCompleted);
        flow.Update(3, 10);
        Assert.Equal(10, await remaining.WaitAsync(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task ClosingAConnectionReleasesBothKindsOfFlowControlWait()
    {
        var flow = new SendFlowControl();
        flow.Open(1);
        flow.Open(3);
        await flow.ReserveAsync(1, Frames.InitialWindow, CancellationToken.None);
        var streamWait     = flow.ReserveAsync(1, 1, CancellationToken.None).AsTask();
        var connectionWait = flow.ReserveAsync(3, 1, CancellationToken.None).AsTask();
        flow.Close();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => streamWait.WaitAsync(TimeSpan.FromSeconds(3)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connectionWait.WaitAsync(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task RefundedConnectionCreditCanBeUsedByAnotherStream()
    {
        var flow = new SendFlowControl();
        flow.Open(1);
        flow.Open(3);
        await flow.ReserveAsync(1, Frames.InitialWindow, CancellationToken.None);
        var pending = flow.ReserveAsync(3, 20, CancellationToken.None).AsTask();
        flow.Remove(1);
        Assert.False(pending.IsCompleted);
        flow.Refund(1, 20);
        Assert.Equal(20, await pending.WaitAsync(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task IncreasingInitialWindowReleasesPreviouslyBlockedStreams()
    {
        var flow = new SendFlowControl();
        flow.UpdateInitialWindow(0);
        flow.Open(1);
        flow.Open(3);
        var first  = flow.ReserveAsync(1, 10, CancellationToken.None).AsTask();
        var second = flow.ReserveAsync(3, 10, CancellationToken.None).AsTask();
        flow.UpdateInitialWindow(10);
        var granted = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal([10, 10], granted);
    }

    [Fact]
    public async Task ConcurrentWaitersShareConnectionCreditWithoutOverspending()
    {
        var flow = new SendFlowControl();
        flow.Open(1);
        await flow.ReserveAsync(1, Frames.InitialWindow, CancellationToken.None);
        var pending = Enumerable.Range(1, 64).Select(index =>
        {
            var id = index * 2 + 1;
            flow.Open(id);
            return flow.ReserveAsync(id, 100, CancellationToken.None).AsTask();
        }).ToArray();
        flow.Update(0, 6400);
        Assert.Equal(6400, (await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(3))).Sum());
        using var cancellation = new CancellationTokenSource();
        var       exhausted    = flow.ReserveAsync(3, 1, cancellation.Token).AsTask();
        Assert.False(exhausted.IsCompleted);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => exhausted);
    }

    [Fact]
    public async Task StalledWritesCancelTheWholeConnection()
    {
        using var       connection = new CancellationTokenSource();
        await using var stream     = new StalledWriteStream();
        await using var writer     = new FrameWriter(stream, connection, TimeSpan.FromMilliseconds(60));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writer.WriteAsync(Frames.Data, 0, 1, new byte[16], CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.True(connection.IsCancellationRequested);
    }

    [Fact]
    public async Task RequestCancellationCannotSplitAnAlreadyStartedFrame()
    {
        using var       connection = new CancellationTokenSource();
        using var       request    = new CancellationTokenSource();
        await using var stream     = new CancelAfterHeaderStream(request);
        await using var writer     = new FrameWriter(stream, connection, TimeSpan.FromSeconds(3));
        await writer.WriteAsync(Frames.Data, 0, 1, "complete"u8.ToArray(), request.Token);
        stream.Position = 0;
        var frame = await Frames.ReadAsync(stream, CancellationToken.None);
        Assert.Equal("complete"u8.ToArray(), frame!.Value.Payload.ToArray());
        Assert.False(connection.IsCancellationRequested);
    }

    [Fact]
    public async Task ConcurrentFrameWritesRetainTheirPayloadUntilTheTransportCompletes()
    {
        using var       connection = new CancellationTokenSource();
        await using var stream     = new DelayedWriteStream();
        await using var writer     = new FrameWriter(stream, connection, TimeSpan.FromSeconds(5));
        await Task.WhenAll(Enumerable.Range(1, 32).Select(id => writer.WriteAsync(Frames.Data, 0, id,
            Enumerable.Repeat((byte)id, 1000 + id).ToArray(), CancellationToken.None).AsTask()));
        stream.Position = 0;
        var ids = new HashSet<int>();
        while (await Frames.ReadAsync(stream, CancellationToken.None) is {} frame)
        {
            Assert.True(ids.Add(frame.StreamId));
            Assert.Equal(Frames.Data, frame.Type);
            Assert.Equal(Enumerable.Repeat((byte)frame.StreamId, 1000 + frame.StreamId), frame.Payload.ToArray());
        }

        Assert.Equal(32, ids.Count);
    }

    [Theory]
    [InlineData(16484)]
    [InlineData(81920)]
    public async Task LargeHeaderBlocksKeepContinuationFramesTogetherDuringConcurrentWrites(int length)
    {
        using var       connection = new CancellationTokenSource();
        await using var stream     = new DelayedWriteStream();
        await using var writer     = new FrameWriter(stream, connection, TimeSpan.FromSeconds(5));
        var             block      = new byte[length];
        Random.Shared.NextBytes(block);
        await Task.WhenAll(writer.WriteHeadersAsync(1, block, true, CancellationToken.None).AsTask(),
            writer.WriteAsync(Frames.Data, Frames.EndStream, 3, "other"u8.ToArray(), CancellationToken.None).AsTask());
        stream.Position = 0;
        var first = (await Frames.ReadAsync(stream, CancellationToken.None))!.Value;
        Assert.Equal(Frames.Headers, first.Type);
        Assert.Equal(Frames.EndStream, first.Flags);
        var   payload = new List<byte>(first.Payload.ToArray());
        Frame continuation;
        do
        {
            continuation = (await Frames.ReadAsync(stream, CancellationToken.None))!.Value;
            Assert.Equal(Frames.Continuation, continuation.Type);
            Assert.Equal(1, continuation.StreamId);
            Assert.Equal(0, continuation.Flags & Frames.EndStream);
            payload.AddRange(continuation.Payload.ToArray());
        } while ((continuation.Flags & Frames.EndHeaders) == 0);

        Assert.Equal(block, payload);
        Assert.Equal(3, (await Frames.ReadAsync(stream, CancellationToken.None))!.Value.StreamId);
    }

    [Fact]
    public async Task ReadyFramesAreBatchedWithoutCompletingBeforeTheyAreWritten()
    {
        using var       connection = new CancellationTokenSource();
        await using var stream     = new PausedWriteStream();
        await using var writer     = new FrameWriter(stream, connection, TimeSpan.FromSeconds(5));
        var             first      = writer.WriteAsync(Frames.Data, 0, 1, new byte[100], CancellationToken.None).AsTask();
        await stream.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var pending = Enumerable.Range(2, 32).Select(id => writer.WriteAsync(Frames.Data, 0, id, new byte[100], CancellationToken.None).AsTask()).ToArray();
        Assert.All(pending, task => Assert.False(task.IsCompleted));
        stream.Resume.TrySetResult();
        await Task.WhenAll(pending.Append(first)).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.InRange(stream.Writes, 2, 4);
        stream.Position = 0;
        for (var id = 1; id <= 33; id++)
        {
            Assert.Equal(id, (await Frames.ReadAsync(stream, CancellationToken.None))!.Value.StreamId);
        }
    }

    [Fact]
    public async Task CancellationRemovesQueuedWritesWhileAnotherWriteIsBlocked()
    {
        using var       connection   = new CancellationTokenSource();
        using var       cancellation = new CancellationTokenSource();
        await using var stream       = new PausedWriteStream();
        await using var writer       = new FrameWriter(stream, connection, TimeSpan.FromSeconds(5));
        var             first        = writer.WriteAsync(Frames.Data, 0, 1, new byte[100], CancellationToken.None).AsTask();
        await stream.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var pending = writer.WriteAsync(Frames.Data, 0, 3, new byte[100], cancellation.Token).AsTask();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(3)));
        stream.Resume.TrySetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(3));
        await writer.WriteAsync(Frames.Data, 0, 5, new byte[100], CancellationToken.None);
        stream.Position = 0;
        Assert.Equal(1, (await Frames.ReadAsync(stream, CancellationToken.None))!.Value.StreamId);
        Assert.Equal(5, (await Frames.ReadAsync(stream, CancellationToken.None))!.Value.StreamId);
        Assert.Null(await Frames.ReadAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task ConnectionCancellationReleasesWritersBeyondTheQueueCapacity()
    {
        using var       connection = new CancellationTokenSource();
        await using var stream     = new StalledWriteStream();
        await using var writer     = new FrameWriter(stream, connection, TimeSpan.FromSeconds(5));
        var             pending    = Enumerable.Range(1, 300).Select(id => writer.WriteAsync(Frames.Data, 0, id, new byte[100], CancellationToken.None).AsTask()).ToArray();
        await connection.CancelAsync();
        foreach (var task in pending)
        {
            var exception = await Record.ExceptionAsync(() => task.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.NotNull(exception);
            Assert.True(exception is IOException or OperationCanceledException, exception.ToString());
        }
    }

    [Fact]
    public async Task ControlFramesDoNotWaitForBlockedApplicationWritesAndOwnTheirPayload()
    {
        using var       connection = new CancellationTokenSource();
        await using var stream     = new PausedWriteStream();
        await using var writer     = new FrameWriter(stream, connection, TimeSpan.FromSeconds(5));
        var             first      = writer.WriteAsync(Frames.Data, 0, 1, new byte[100], CancellationToken.None).AsTask();
        await stream.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var pending = Enumerable.Range(2, 300).Select(id => writer.WriteAsync(Frames.Data, 0, id, new byte[100], CancellationToken.None).AsTask()).ToArray();
        var ping    = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        await writer.QueueControlAsync(Frames.Ping, Frames.Ack, 0, ping).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        ping.AsSpan().Clear();
        stream.Resume.TrySetResult();
        await Task.WhenAll(pending.Append(first)).WaitAsync(TimeSpan.FromSeconds(3));
        stream.Position = 0;
        var found = false;
        while (await Frames.ReadAsync(stream, CancellationToken.None) is {} frame)
        {
            if (frame.Type == Frames.Ping)
            {
                found = true;
                Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, frame.Payload.ToArray());
            }
        }

        Assert.True(found);
    }

    [Fact]
    public async Task ABlockedPeerCannotAccumulateAnUnboundedControlQueue()
    {
        using var       connection = new CancellationTokenSource();
        await using var stream     = new PausedWriteStream();
        await using var writer     = new FrameWriter(stream, connection, TimeSpan.FromSeconds(5));
        var             first      = writer.WriteAsync(Frames.Data, 0, 1, new byte[100], CancellationToken.None).AsTask();
        await stream.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        for (var index = 0; index < 256; index++)
        {
            await writer.QueueControlAsync(Frames.Ping, Frames.Ack, 0, new byte[8]);
        }

        await Assert.ThrowsAsync<IOException>(() => writer.QueueControlAsync(Frames.Ping, Frames.Ack, 0, new byte[8]).AsTask());
        Assert.True(connection.IsCancellationRequested);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WaitAsync(TimeSpan.FromSeconds(3)));
    }

    private sealed class PausedWriteStream : MemoryStream
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume  { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int                  Writes  { get; private set; }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Writes++;
            Started.TrySetResult();
            await Resume.Task.WaitAsync(cancellationToken);
            await base.WriteAsync(buffer, cancellationToken);
        }
    }

    private sealed class DelayedWriteStream : MemoryStream
    {
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            await base.WriteAsync(buffer, cancellationToken);
        }
    }

    private sealed class StalledWriteStream : MemoryStream
    {
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => await Task.Delay(Timeout.Infinite, cancellationToken);
    }

    private sealed class CancelAfterHeaderStream(CancellationTokenSource request) : MemoryStream
    {
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await base.WriteAsync(buffer, cancellationToken);
            await request.CancelAsync();
        }
    }
}
