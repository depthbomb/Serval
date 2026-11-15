#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Serval.Generated;
using Serval.Hosting;
using Serval.Tests.Support;
using Serval.Transport.Http2;
using Serval.Transport.Http2.Protocol;

namespace Serval.Tests.Transports.Http2;

public sealed class Http2WireTests : IAsyncLifetime
{
    private          IHost?             _host;
    private          int                _port;
    private readonly EarlyHintsEndpoint _earlyHints = new();

    public async Task InitializeAsync()
    {
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        _port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddScoped<ScopedProbe>();
        builder.Services.AddServal().AddGeneratedControllers()
               .AddController<object>("GET", "/early-hints", null, (_, context) => _earlyHints.InvokeAsync(context))
               .AddHttp2(options =>
               {
                   options.EndPoint = new IPEndPoint(IPAddress.Loopback, _port);
                   options.UseTls   = false;
               });
        _host = builder.Build();
        await _host.StartAsync();
    }

    public async Task DisposeAsync()
    {
        _earlyHints.Finish.TrySetResult();
        if (_host is not null)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await _host.StopAsync(timeout.Token);
            _host.Dispose();
        }
    }

    [Theory]
    [InlineData(Frames.Ping, 1, "0000000000000000", Http2Error.Protocol)]
    [InlineData(Frames.Ping, 0, "", Http2Error.FrameSize)]
    [InlineData(Frames.Settings, 1, "", Http2Error.Protocol)]
    [InlineData(Frames.Settings, 0, "0000000000", Http2Error.FrameSize)]
    [InlineData(Frames.Settings, 0, "000200000002", Http2Error.Protocol)]
    [InlineData(Frames.Settings, 0, "000480000000", Http2Error.FlowControl)]
    [InlineData(Frames.Settings, 0, "000500003fff", Http2Error.Protocol)]
    [InlineData(Frames.Continuation, 1, "", Http2Error.Protocol)]
    [InlineData(Frames.Headers, 2, "828684", Http2Error.Protocol)]
    [InlineData(Frames.Headers, 1, "80", Http2Error.Compression)]
    [InlineData(Frames.Data, 3, "", Http2Error.Protocol)]
    [InlineData(Frames.WindowUpdate, 0, "00000000", Http2Error.Protocol)]
    [InlineData(Frames.ResetStream, 0, "00000000", Http2Error.Protocol)]
    [InlineData(Frames.PushPromise, 1, "", Http2Error.Protocol)]
    public async Task MalformedFramesProduceGoAway(byte type, int streamId, string encoded, uint expected)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var client  = await ConnectAsync(timeout.Token);
        var       stream  = client.GetStream();
        await WriteAsync(stream, type, type == Frames.Headers ? (byte)5 : (byte)0, streamId, Convert.FromHexString(encoded), timeout.Token);
        var frame = await ReadUntilAsync(stream, Frames.GoAway, timeout.Token);
        Assert.Equal(expected, BinaryPrimitives.ReadUInt32BigEndian(frame.Payload.Span[4..]));
    }

    [Fact]
    public async Task HeaderContinuationCannotBeInterleaved()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var client  = await ConnectAsync(timeout.Token);
        var       stream  = client.GetStream();
        await WriteAsync(stream, Frames.Headers, 0, 1, [0x82], timeout.Token);
        await WriteAsync(stream, Frames.Ping, 0, 0, new byte[8], timeout.Token);
        var error = await ReadUntilAsync(stream, Frames.GoAway, timeout.Token);
        Assert.Equal(Http2Error.Protocol, BinaryPrimitives.ReadUInt32BigEndian(error.Payload.Span[4..]));
    }

    [Fact]
    public async Task UnknownFramesAreIgnoredAndPingIsAcknowledged()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var client  = await ConnectAsync(timeout.Token);
        var       stream  = client.GetStream();
        await WriteAsync(stream, 0xfa, 0, 0, [1, 2, 3], timeout.Token);
        var ping = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        await WriteAsync(stream, Frames.Ping, 0, 0, ping, timeout.Token);
        var pong = await ReadUntilAsync(stream, Frames.Ping, timeout.Token);
        Assert.Equal(Frames.Ack, pong.Flags);
        Assert.Equal(ping, pong.Payload.ToArray());
    }

    [Fact]
    public async Task InvalidRequestHeadersResetOnlyTheirStream()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var client  = await ConnectAsync(timeout.Token);
        var       stream  = client.GetStream();
        var       invalid = RequestBlock("/test/task").Concat(new byte[] { 0x84 }).ToArray();
        await WriteAsync(stream, Frames.Headers, 5, 1, invalid, timeout.Token);
        var reset = await ReadUntilAsync(stream, Frames.ResetStream, timeout.Token);
        Assert.Equal(1, reset.StreamId);
        Assert.Equal(Http2Error.Protocol, BinaryPrimitives.ReadUInt32BigEndian(reset.Payload.Span));
        await WriteAsync(stream, Frames.Headers, 5, 3, RequestBlock("/test/task"), timeout.Token);
        var headers = await ReadUntilAsync(stream, Frames.Headers, timeout.Token);
        Assert.Equal(3, headers.StreamId);
        Assert.Contains((":status", "200"), new HPack().Decode(headers.Payload.Span, 4096));
    }

    [Fact]
    public async Task RejectedPriorityDoesNotPermitReusingAStreamId()
    {
        using var timeout        = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var client         = await ConnectAsync(timeout.Token);
        var       stream         = client.GetStream();
        var       selfDependency = new byte[] { 0, 0, 0, 1, 0 }.Concat(RequestBlock("/test/task")).ToArray();
        await WriteAsync(stream, Frames.Headers, 0x25, 1, selfDependency, timeout.Token);
        var first = await ReadUntilAsync(stream, Frames.ResetStream, timeout.Token);
        Assert.Equal(Http2Error.Protocol, BinaryPrimitives.ReadUInt32BigEndian(first.Payload.Span));
        await WriteAsync(stream, Frames.Headers, 5, 1, RequestBlock("/test/task"), timeout.Token);
        var reused = await ReadUntilAsync(stream, Frames.ResetStream, timeout.Token);
        Assert.Equal(Http2Error.StreamClosed, BinaryPrimitives.ReadUInt32BigEndian(reused.Payload.Span));
    }

    [Fact]
    public async Task BlockedResponsesAllowControlFramesAndResumeWithWindowCredit()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var client  = await ConnectAsync(timeout.Token, true);
        var       stream  = client.GetStream();
        await WriteAsync(stream, Frames.Headers, 5, 1, RequestBlock("/test/task"), timeout.Token);
        await ReadUntilAsync(stream, Frames.Headers, timeout.Token);
        await WriteAsync(stream, Frames.Ping, 0, 0, new byte[8], timeout.Token);
        var next = await Frames.ReadAsync(stream, timeout.Token);
        Assert.Equal(Frames.Ping, next!.Value.Type);
        var credit = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(credit, 4);
        await WriteAsync(stream, Frames.WindowUpdate, 0, 1, credit, timeout.Token);
        var data = await ReadUntilAsync(stream, Frames.Data, timeout.Token);
        Assert.Equal("task", Encoding.UTF8.GetString(data.Payload.Span));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ResetFloodsInEitherDirectionCloseTheConnection(bool clientResets)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var client  = await ConnectAsync(timeout.Token);
        var       stream  = client.GetStream();
        var       block   = RequestBlock("/test/task");
        if (clientResets)
        {
            await WriteAsync(stream, Frames.Headers, 5, 1, block, timeout.Token);
            for (var index = 0; index < 101; index++)
            {
                await WriteAsync(stream, Frames.ResetStream, 0, 1, [0, 0, 0, 8], timeout.Token);
            }
        }
        else
        {
            var invalid = block.Concat(new byte[] { 0x84 }).ToArray();
            for (var index = 0; index < 101; index++)
            {
                await WriteAsync(stream, Frames.Headers, 5, index * 2 + 1, invalid, timeout.Token);
            }
        }

        var goAway = await ReadUntilAsync(stream, Frames.GoAway, timeout.Token);
        Assert.Equal(Http2Error.EnhanceYourCalm, BinaryPrimitives.ReadUInt32BigEndian(goAway.Payload.Span[4..]));
    }

    [Fact]
    public async Task EarlyResponsesStopUnreadUploadsAndPreserveTheConnection()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var client  = await ConnectAsync(timeout.Token);
        var       stream  = client.GetStream();
        await WriteAsync(stream, Frames.Headers, Frames.EndHeaders, 1, RequestBlock("/test/task"), timeout.Token);
        var reset = await ReadUntilAsync(stream, Frames.ResetStream, timeout.Token);
        Assert.Equal(1, reset.StreamId);
        Assert.Equal(Http2Error.NoError, BinaryPrimitives.ReadUInt32BigEndian(reset.Payload.Span));
        await WriteAsync(stream, Frames.Headers, 5, 3, RequestBlock("/test/task"), timeout.Token);
        var response = await ReadUntilAsync(stream, Frames.Headers, timeout.Token);
        Assert.Equal(3, response.StreamId);
        Assert.Contains((":status", "200"), new HPack().Decode(response.Payload.Span, 4096));
    }

    [Theory]
    [InlineData("x-value", " bad")]
    [InlineData("te", "trailers")]
    public async Task InvalidTrailersResetTheirStream(string name, string value)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var client  = await ConnectAsync(timeout.Token);
        var       stream  = client.GetStream();
        // /test/slow keeps the request alive until the trailer validation runs.
        await WriteAsync(stream, Frames.Headers, Frames.EndHeaders, 1, RequestBlock("/test/slow"), timeout.Token);
        byte[] trailer = [0, (byte)name.Length, .. Encoding.ASCII.GetBytes(name), (byte)value.Length, .. Encoding.ASCII.GetBytes(value)];
        await WriteAsync(stream, Frames.Headers, 5, 1, trailer, timeout.Token);
        var reset = await ReadUntilAsync(stream, Frames.ResetStream, timeout.Token);
        Assert.Equal(Http2Error.Protocol, BinaryPrimitives.ReadUInt32BigEndian(reset.Payload.Span));
    }

    [Theory]
    [InlineData("GET", false)]
    [InlineData("HEAD", false)]
    [InlineData("GET", true)]
    public async Task EarlyHintsPrecedeTheFinalResponseWithoutEndingTheStream(string method, bool fail)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var client  = await ConnectAsync(timeout.Token);
        var       stream  = client.GetStream();
        var       block   = RequestBlock(fail ? "/early-hints?fail=true" : "/early-hints");
        if (method == "HEAD")
        {
            block = [0x02, 4, .. "HEAD"u8, .. block[1..]];
        }

        await WriteAsync(stream, Frames.Headers, 5, 1, block, timeout.Token);
        var decoder = new HPack();
        for (var index = 0; index < 2; index++)
        {
            var hint = await ReadUntilAsync(stream, Frames.Headers, timeout.Token);
            Assert.Equal(1, hint.StreamId);
            Assert.Equal(Frames.EndHeaders, hint.Flags);
            var fields = decoder.Decode(hint.Payload.Span, 4096);
            Assert.Contains((":status", "103"), fields);
            Assert.DoesNotContain(fields, field => field.Name is "x-final" or "content-length");
            Assert.Equal(index == 0 ? [EarlyHintsEndpoint.Stylesheet, EarlyHintsEndpoint.Script] : new[] { EarlyHintsEndpoint.Font },
                fields.Where(field => field.Name == "link").Select(field => field.Value));
        }

        _earlyHints.Finish.SetResult();
        var final       = await ReadUntilAsync(stream, Frames.Headers, timeout.Token);
        var finalFields = decoder.Decode(final.Payload.Span, 4096);
        Assert.Contains((":status", fail ? "422" : "200"), finalFields);
        Assert.Equal(!fail, finalFields.Contains(("x-final", "only")));
        Assert.DoesNotContain(finalFields, field => field.Name == "link");
        if (method == "HEAD")
        {
            Assert.NotEqual(0, final.Flags & Frames.EndStream);
            return;
        }

        using var body = new MemoryStream();
        Frame     data;
        do
        {
            data = await ReadUntilAsync(stream, Frames.Data, timeout.Token);
            Assert.Equal(1, data.StreamId);
            body.Write(data.Payload.Span);
        } while ((data.Flags & Frames.EndStream) == 0);

        Assert.Contains(fail ? "hinted failure" : "complete", Encoding.UTF8.GetString(body.ToArray()));
    }

    private async Task<TcpClient> ConnectAsync(CancellationToken cancellationToken, bool zeroWindow = false)
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, _port, cancellationToken);
        var stream = client.GetStream();
        await stream.WriteAsync("PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n"u8.ToArray(), cancellationToken);
        await WriteAsync(stream, Frames.Settings, 0, 0, zeroWindow ? [0, 4, 0, 0, 0, 0] : [], cancellationToken);
        var server = await ReadUntilAsync(stream, Frames.Settings, cancellationToken);
        Assert.Equal(0, server.Flags);
        await WriteAsync(stream, Frames.Settings, Frames.Ack, 0, [], cancellationToken);
        var ack = await ReadUntilAsync(stream, Frames.Settings, cancellationToken);
        Assert.Equal(Frames.Ack, ack.Flags);
        return client;
    }

    private static async Task<Frame> ReadUntilAsync(Stream stream, byte type, CancellationToken cancellationToken)
    {
        while (await Frames.ReadAsync(stream, cancellationToken) is { } frame)
        {
            if (frame.Type == type)
            {
                return frame;
            }
        }

        throw new EndOfStreamException($"The connection closed before frame {type}.");
    }

    private static async Task WriteAsync(Stream stream, byte type, byte flags, int streamId, byte[] payload, CancellationToken cancellationToken)
    {
        await stream.WriteAsync(Frames.Header(type, flags, streamId, payload.Length), cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
    }

    private static byte[] RequestBlock(string path) => [0x82, 0x86, 0x01, 9, .. "localhost"u8, 0x04, (byte)path.Length, .. Encoding.ASCII.GetBytes(path)];
}
