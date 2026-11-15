#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Serval.Hosting;
using Serval.Http;
using Serval.Http.Results;
using Serval.Transport.Http2;
using Serval.Transport.Http2.Protocol;

namespace Serval.Tests.Transports.Http2;

public sealed class Http2ReceiveWindowTests : IAsyncLifetime
{
    private IHost? _host;
    private int    _port;

    public async Task InitializeAsync()
    {
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        _port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        var app = builder.Services.AddServal();
        app.AddController<object>("POST", "/upload", null, async (_, context) =>
        {
            var body = await context.Request.ReadBodyAsync();
            return new TextResult(body.Length.ToString(CultureInfo.InvariantCulture));
        });
        app.AddController<object>("GET", "/early", null, (_, context) => ValueTask.FromResult<IActionResult>(new TextResult("early")));
        app.AddHttp2(options =>
        {
            options.EndPoint = new IPEndPoint(IPAddress.Loopback, _port);
            options.UseTls   = false;
        });
        _host = builder.Build();
        await _host.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_host is not null)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await _host.StopAsync(timeout.Token);
            if (_host is IAsyncDisposable disposable)
            {
                await disposable.DisposeAsync();
            }
            else
            {
                _host.Dispose();
            }
        }
    }

    [Theory]
    [InlineData(512, false)]
    [InlineData(512, true)]
    [InlineData(16384, false)]
    public async Task FragmentedUploadsMakeProgressWithoutAnUpdateForEveryFragment(int fragmentSize, bool padded)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var client  = await ConnectAsync(timeout.Token);
        var       stream  = client.GetStream();
        await WriteAsync(stream, Frames.Headers, Frames.EndHeaders, 1, RequestBlock("/upload", true), timeout.Token);
        const int length           = 200_000;
        var       sent             = 0;
        var       connectionCredit = Frames.InitialWindow;
        var       streamCredit     = Frames.InitialWindow;
        var       updates          = 0;
        var       padBytes         = padded ? 8 : 0;
        while (sent < length)
        {
            while (Math.Min(connectionCredit, streamCredit) <= padBytes)
            {
                var frame = await ReadAsync(stream, timeout.Token);
                Assert.Equal(Frames.WindowUpdate, frame.Type);
                ApplyCredit(frame);
            }

            var count   = Math.Min(length - sent, Math.Min(fragmentSize - padBytes, Math.Min(connectionCredit, streamCredit) - padBytes));
            var payload = new byte[count + padBytes];
            if (padded)
            {
                payload[0] = (byte)(padBytes - 1);
            }

            var flags = padded ? Frames.Padded : (byte)0;
            if (sent + count == length)
            {
                flags |= Frames.EndStream;
            }

            await WriteAsync(stream, Frames.Data, flags, 1, payload, timeout.Token);
            connectionCredit -= payload.Length;
            streamCredit     -= payload.Length;
            sent             += count;
        }

        using var response = new MemoryStream();
        while (true)
        {
            var frame = await ReadAsync(stream, timeout.Token);
            if (frame.Type == Frames.WindowUpdate)
            {
                ApplyCredit(frame);
                continue;
            }

            Assert.Equal(1, frame.StreamId);
            if (frame.Type == Frames.Headers)
            {
                Assert.Contains((":status", "200"), new HPack().Decode(frame.Payload.Span, 4096));
            }
            else
            {
                Assert.Equal(Frames.Data, frame.Type);
                await response.WriteAsync(frame.Payload, timeout.Token);
            }

            if ((frame.Flags & Frames.EndStream) != 0)
            {
                break;
            }
        }

        Assert.Equal("200000", Encoding.UTF8.GetString(response.ToArray()));
        Assert.InRange(updates, 2, 20);

        void ApplyCredit(Frame frame)
        {
            var increment = BinaryPrimitives.ReadInt32BigEndian(frame.Payload.Span);
            Assert.InRange(increment, Frames.InitialWindow / 2, Frames.InitialWindow);
            if (frame.StreamId == 0)
            {
                connectionCredit += increment;
            }
            else
            {
                Assert.Equal(1, frame.StreamId);
                streamCredit += increment;
            }

            Assert.InRange(connectionCredit, 0, Frames.InitialWindow);
            Assert.InRange(streamCredit, 0, Frames.InitialWindow);
            updates++;
        }
    }

    [Fact]
    public async Task SmallCompletedUploadsShareConnectionCreditAcrossStreamBoundaries()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var client  = await ConnectAsync(timeout.Token);
        var       stream  = client.GetStream();
        var       credit  = Frames.InitialWindow;
        var       updates = 0;
        for (var request = 0; request < 80; request++)
        {
            var id = request * 2 + 1;
            Assert.True(credit >= 1000);
            await WriteAsync(stream, Frames.Headers, Frames.EndHeaders, id, RequestBlock("/upload", true), timeout.Token);
            await WriteAsync(stream, Frames.Data, Frames.EndStream, id, new byte[1000], timeout.Token);
            credit -= 1000;
            while (true)
            {
                var frame = await ReadAsync(stream, timeout.Token);
                if (frame.Type == Frames.WindowUpdate)
                {
                    Assert.Equal(0, frame.StreamId);
                    credit += BinaryPrimitives.ReadInt32BigEndian(frame.Payload.Span);
                    Assert.InRange(credit, 0, Frames.InitialWindow);
                    updates++;
                    continue;
                }

                Assert.Equal(id, frame.StreamId);
                Assert.True(frame.Type is Frames.Headers or Frames.Data);
                if ((frame.Flags & Frames.EndStream) != 0)
                {
                    break;
                }
            }
        }

        Assert.InRange(updates, 1, 4);
    }

    [Fact]
    public async Task InFlightDataAfterResetStillReplenishesConnectionCredit()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var client  = await ConnectAsync(timeout.Token);
        var       stream  = client.GetStream();
        await WriteAsync(stream, Frames.Headers, Frames.EndHeaders, 1, RequestBlock("/early", false), timeout.Token);
        while ((await ReadAsync(stream, timeout.Token)).Type != Frames.ResetStream) { }

        var remaining = Frames.InitialWindow;
        while (remaining > 0)
        {
            var count = Math.Min(remaining, Frames.MaximumPayload);
            await WriteAsync(stream, Frames.Data, 0, 1, new byte[count], timeout.Token);
            remaining -= count;
        }

        await WriteAsync(stream, Frames.Ping, 0, 0, new byte[8], timeout.Token);
        var returned = 0;
        while (true)
        {
            var frame = await ReadAsync(stream, timeout.Token);
            if (frame.Type == Frames.Ping)
            {
                Assert.Equal(Frames.Ack, frame.Flags);
                break;
            }

            Assert.Equal(Frames.WindowUpdate, frame.Type);
            Assert.Equal(0, frame.StreamId);
            returned += BinaryPrimitives.ReadInt32BigEndian(frame.Payload.Span);
        }

        Assert.Equal(Frames.InitialWindow, returned);
    }

    private async Task<TcpClient> ConnectAsync(CancellationToken token)
    {
        var client = new TcpClient { NoDelay = true };
        await client.ConnectAsync(IPAddress.Loopback, _port, token);
        var stream = client.GetStream();
        await stream.WriteAsync("PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n"u8.ToArray(), token);
        await WriteAsync(stream, Frames.Settings, 0, 0, [], token);
        Assert.Equal(Frames.Settings, (await ReadAsync(stream, token)).Type);
        await WriteAsync(stream, Frames.Settings, Frames.Ack, 0, [], token);
        var ack = await ReadAsync(stream, token);
        Assert.Equal(Frames.Settings, ack.Type);
        Assert.Equal(Frames.Ack, ack.Flags);
        return client;
    }

    private static async Task<Frame> ReadAsync(Stream stream, CancellationToken token) => await Frames.ReadAsync(stream, token) ?? throw new EndOfStreamException();

    private static async Task WriteAsync(Stream stream, byte type, byte flags, int id, byte[] payload, CancellationToken token)
    {
        var frame = new byte[9 + payload.Length];
        Frames.WriteHeader(frame, type, flags, id, payload.Length);
        payload.CopyTo(frame, 9);
        await stream.WriteAsync(frame, token);
    }

    private static byte[] RequestBlock(string path, bool post) => [(byte)(post ? 0x83 : 0x82), 0x86, 0x01, 9, .. "localhost"u8, 0x04, (byte)path.Length, .. Encoding.ASCII.GetBytes(path)];
}
