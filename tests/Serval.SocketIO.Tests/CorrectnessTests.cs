using System.Reflection;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Threading.Channels;
using Serval.Http;
using Serval.SocketIO.Internal;

namespace Serval.SocketIO.Tests;

public sealed class CorrectnessTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PollingReadRemainsExclusiveUntilResponseCompletes(bool blockCompletion)
    {
        await using var host = await TestServer.StartAsync();
        var session = new EngineSession(host.Server, new ClaimsPrincipal(), false);
        session.Enqueue(EnginePacket.Text(blockCompletion ? "6" : "42[\"test\"]"));
        var response = new CapturedResponse(block: true) { BlockCompletion = blockCompletion };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var pending = session.PollAsync(response, timeout.Token).AsTask();
        try
        {
            await response.Writing.Task.WaitAsync(TimeSpan.FromSeconds(blockCompletion ? 3 : 1), timeout.Token);
            var exception = await Assert.ThrowsAsync<HttpErrorException>(() => session.PollAsync(new CapturedResponse(), timeout.Token).AsTask());
            Assert.Equal(400, exception.StatusCode);
        }
        finally
        {
            response.Release.TrySetResult();
            session.Close("test complete");
            try { await pending; } catch (OperationCanceledException) when (!blockCompletion) { }
        }
    }

    [Fact]
    public async Task ReconstructedBinaryLimitIncludesTheEnginePacketPrefix()
    {
        await using var host = await TestServer.StartAsync(options => options.MaxPayloadBytes = 100);
        var session = new EngineSession(host.Server, new ClaimsPrincipal(), false);
        var header = EnginePacket.Text("451-[\"binary\",{\"_placeholder\":true,\"num\":0}]");
        await session.ReceiveAsync(header, CancellationToken.None);
        var attachment = new EnginePacket(new byte[101 - header.Data.Length], true);
        await Assert.ThrowsAsync<InvalidDataException>(() => session.ReceiveAsync(attachment, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task CompletedSessionReleasesQueuedPayloadsAndPartialAttachments()
    {
        await using var host = await TestServer.StartAsync();
        var session = new EngineSession(host.Server, new ClaimsPrincipal(), false);
        session.Start();
        session.Enqueue(new EnginePacket(new byte[8192], true));
        await session.ReceiveAsync(EnginePacket.Text("452-[\"binary\",{\"_placeholder\":true,\"num\":0},{\"_placeholder\":true,\"num\":1}]"), CancellationToken.None);
        await session.ReceiveAsync(new EnginePacket(new byte[4096], true), CancellationToken.None);
        session.Close("test complete");
        await session.Completion;
        Assert.False(Field<Channel<EnginePacket>>(session, "_outgoing").Reader.TryRead(out _));
        Assert.Empty(Field<List<byte[]>>(session, "_attachments"));
        Assert.Null(Field<SocketPacket?>(session, "_pendingBinary"));
    }

    [Fact]
    public async Task NamespaceDisconnectCancelsItsHandlerWithoutClosingOtherNamespaces()
    {
        await using var host = await TestServer.StartAsync();
        var session = new EngineSession(host.Server, new ClaimsPrincipal(), false);
        session.Start();
        try
        {
            await session.ReceiveAsync(EnginePacket.Text("40"), CancellationToken.None);
            await session.ReceiveAsync(EnginePacket.Text("40/chat,"), CancellationToken.None);
            await UntilAsync(() => session.Sockets.Count == 2);
            await session.ReceiveAsync(EnginePacket.Text("42[\"wait\"]"), CancellationToken.None);
            var root = session.Sockets["/"];
            await UntilAsync(() => host.Scopes.Waiting.Contains(root.Id));
            root.Disconnect();
            Assert.True(root.ConnectionClosed.IsCancellationRequested);
            await UntilAsync(() => !session.Sockets.ContainsKey("/"));
            Assert.False(session.Closed.IsCancellationRequested);
            Assert.True(session.Sockets["/chat"].IsConnected);
            await session.ReceiveAsync(EnginePacket.Text("42[\"wait\"]"), CancellationToken.None);
            await session.ReceiveAsync(EnginePacket.Text("42/chat,1[\"echo\",\"alive\"]"), CancellationToken.None);
            Assert.Equal("43/chat,1[\"alive\"]", await ReadMatchingTextAsync(session, "43/chat,1"));
        }
        finally
        {
            session.Close("test complete");
            await session.Completion;
        }
    }

    [Fact]
    public async Task NamespaceReconnectDoesNotReuseAcknowledgementIds()
    {
        await using var host = await TestServer.StartAsync();
        var session = new EngineSession(host.Server, new ClaimsPrincipal(), false);
        session.Start();
        try
        {
            await session.ReceiveAsync(EnginePacket.Text("40"), CancellationToken.None);
            await UntilAsync(() => session.Sockets.ContainsKey("/"));
            using var cancel = new CancellationTokenSource();
            var first = session.Sockets["/"].EmitWithAcknowledgementAsync("ask", [], ct: cancel.Token).AsTask();
            var firstId = await NextEventIdAsync(session);
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
            await session.ReceiveAsync(EnginePacket.Text("41"), CancellationToken.None);
            await UntilAsync(() => !session.Sockets.ContainsKey("/"));
            await session.ReceiveAsync(EnginePacket.Text("40"), CancellationToken.None);
            await UntilAsync(() => session.Sockets.ContainsKey("/"));
            using var cancelSecond = new CancellationTokenSource();
            var second = session.Sockets["/"].EmitWithAcknowledgementAsync("ask", [], ct: cancelSecond.Token).AsTask();
            var secondId = await NextEventIdAsync(session);
            try
            {
                Assert.NotEqual(firstId, secondId);
                await session.ReceiveAsync(EnginePacket.Text($"43{firstId}[\"stale\"]"), CancellationToken.None);
                Assert.False(second.IsCompleted);
            }
            finally
            {
                cancelSecond.Cancel();
                try { await second; } catch (OperationCanceledException) { }
            }
        }
        finally
        {
            session.Close("test complete");
            await session.Completion;
        }
    }

    [Fact]
    public async Task ClientNamespaceDisconnectInterruptsItsHandler()
    {
        await using var host = await TestServer.StartAsync();
        var session = new EngineSession(host.Server, new ClaimsPrincipal(), false);
        session.Start();
        try
        {
            await session.ReceiveAsync(EnginePacket.Text("40"), CancellationToken.None);
            await UntilAsync(() => session.Sockets.ContainsKey("/"));
            var socket = session.Sockets["/"];
            await session.ReceiveAsync(EnginePacket.Text("42[\"wait\"]"), CancellationToken.None);
            await UntilAsync(() => host.Scopes.Waiting.Contains(socket.Id));
            await session.ReceiveAsync(EnginePacket.Text("41"), CancellationToken.None);
            await UntilAsync(() => host.Scopes.Disconnected.Contains(socket.Id));
            Assert.True(socket.ConnectionClosed.IsCancellationRequested);
            Assert.False(session.Closed.IsCancellationRequested);
        }
        finally
        {
            session.Close("test complete");
            await session.Completion;
        }
    }

    [Fact]
    public async Task ClosingDoesNotRunBufferedHandlers()
    {
        await using var host = await TestServer.StartAsync();
        var session = new EngineSession(host.Server, new ClaimsPrincipal(), false);
        session.Start();
        await session.ReceiveAsync(EnginePacket.Text("40"), CancellationToken.None);
        await UntilAsync(() => session.Sockets.ContainsKey("/"));
        await session.ReceiveAsync(EnginePacket.Text("42[\"wait\"]"), CancellationToken.None);
        await UntilAsync(() => !host.Scopes.Waiting.IsEmpty);
        var scopes = host.Scopes.Scopes.Count;
        for (var index = 0; index < 10; index++)
        {
            await session.ReceiveAsync(EnginePacket.Text("42[\"wait\"]"), CancellationToken.None);
        }

        session.Close("test complete");
        await session.Completion;
        Assert.Equal(scopes + 1, host.Scopes.Scopes.Count); // Only the disconnect callback creates another scope.
        Assert.Single(host.Scopes.Waiting);
        Assert.All(host.Scopes.Scopes.Values, Assert.True);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.ReceiveAsync(EnginePacket.Text("40"), CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task PollingBatchesPreserveOrderAndRespectEncodedPayloadLimit()
    {
        await using var host = await TestServer.StartAsync(options => options.MaxPayloadBytes = 20);
        var session = new EngineSession(host.Server, new ClaimsPrincipal(), false);
        session.Enqueue(EnginePacket.Text("4éé"), EnginePacket.Text("42[\"a\"]"), new EnginePacket(new byte[10], true), EnginePacket.Text("6"));
        var first = new CapturedResponse();
        var firstPacket = await session.PollAsync(first, CancellationToken.None);
        Assert.Equal("4éé\x1e" + "42[\"a\"]", firstPacket.PollingText);
        Assert.Equal(firstPacket.Data, first.Body.ToArray());
        var second = new CapturedResponse();
        var secondPacket = await session.PollAsync(second, CancellationToken.None);
        Assert.Equal("b" + Convert.ToBase64String(new byte[10]) + "\x1e" + "6", secondPacket.PollingText);
        Assert.True(secondPacket.Data.Length <= 20);
        session.Close("test complete");
    }

    private static async Task<string> ReadMatchingTextAsync(EngineSession session, string prefix)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (true)
        {
            var packet = await session.PollAsync(new CapturedResponse(), timeout.Token);
            foreach (var text in packet.PollingText.Split('\x1e'))
            {
                if (text.StartsWith(prefix, StringComparison.Ordinal)) { return text; }
            }
        }
    }

    [Fact]
    public async Task FailedUpgradeKeepsOwnershipUntilItsWebSocketHasClosed()
    {
        await using var host = await TestServer.StartAsync();
        var session = new EngineSession(host.Server, new ClaimsPrincipal(), false);
        using var socket = new ClosingWebSocket();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        session.BeginUpgrade();
        var upgrade = session.RunWebSocketAsync(socket, true, timeout.Token);
        try
        {
            await socket.Closing.Task.WaitAsync(timeout.Token);
            Assert.False(session.Closed.IsCancellationRequested);
            Assert.Throws<HttpErrorException>(session.BeginUpgrade);
        }
        finally
        {
            socket.Release.TrySetResult();
            await upgrade;
            session.CancelUpgrade();
            session.Close("test complete");
        }
    }

    private sealed class ClosingWebSocket : WebSocket
    {
        internal TaskCompletionSource Closing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private WebSocketState _state = WebSocketState.Open;
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override string? SubProtocol => null;
        public override WebSocketState State => _state;
        public override void Abort() => _state = WebSocketState.Aborted;
        public override void Dispose() => Abort();
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => CloseOutputAsync(closeStatus, statusDescription, cancellationToken);
        public override async Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            Closing.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            _state = WebSocketState.Closed;
        }
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            buffer[0] = 255; // Invalid UTF-8 in the upgrade probe.
            return Task.FromResult(new WebSocketReceiveResult(1, WebSocketMessageType.Text, true));
        }
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private static T Field<T>(EngineSession session, string name)
        => (T)typeof(EngineSession).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;

    private static async Task UntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
        {
            await Task.Delay(5, timeout.Token);
        }
    }

    private static async Task<long> NextEventIdAsync(EngineSession session)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (true)
        {
            var response = new CapturedResponse();
            var packet = await session.PollAsync(response, timeout.Token);
            foreach (var text in packet.PollingText.Split('\x1e'))
            {
                if (text.StartsWith("42", StringComparison.Ordinal))
                {
                    return PacketCodec.Decode(text[1..], session.Options).Id!.Value;
                }
            }
        }
    }

    private sealed class CapturedResponse : Response
    {
        internal TaskCompletionSource Writing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal MemoryStream Body { get; } = new();
        internal bool BlockCompletion { get; init; }

        internal CapturedResponse(bool block = false)
        {
            if (!block) { Release.SetResult(); }
        }

        public override ValueTask FlushAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public override async ValueTask CompleteAsync(CancellationToken ct = default)
        {
            if (BlockCompletion) { Writing.TrySetResult(); await Release.Task.WaitAsync(ct); }
            await StartAsync(ct);
            MarkCompleted();
        }
        public override ValueTask AbortAsync(CancellationToken ct = default) { MarkAborted(); return ValueTask.CompletedTask; }
        protected override ValueTask OnStartAsync(CancellationToken ct) => ValueTask.CompletedTask;
        protected override async ValueTask OnWriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct)
        {
            if (!BlockCompletion) { Writing.TrySetResult(); await Release.Task.WaitAsync(ct); }
            await Body.WriteAsync(bytes, ct);
        }
    }
}
