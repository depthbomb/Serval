#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;
using Serval.Hosting;
using Serval.Http;
using Serval.Http.Constants;
using Serval.Http.Results;
using Serval.Sample.Transports;
using Serval.Tests.Support;
using Serval.Transport.Http3;
using Serval.Transport.Http3.Protocol;

namespace Serval.Tests.Transports.Http3;

[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
public sealed class Http3WireTests
{
    [QuicTheory]
    [InlineData("/bad-request")]
    [InlineData("/short")]
    [InlineData("/long")]
    public async Task MessageErrorsResetOnlyTheAffectedStream(string path)
    {
        if (!QuicListener.IsSupported)
        {
            throw new PlatformNotSupportedException();
        }

        using var timeout     = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var certificate = DevelopmentCertificate.Create();
        using var reservation = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        reservation.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)reservation.LocalEndPoint!).Port;
        reservation.Close();
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddServal()
               .AddController<object>("GET", "/ok", null, (_, context) => ValueTask.FromResult<IActionResult>(new TextResult("healthy")))
               .AddController<object>("GET", "/short", null, (_, context) =>
               {
                   context.Response.Headers[HeaderNames.ContentLength] = "10";
                   return ValueTask.FromResult<IActionResult>(new TextResult("body"));
               })
               .AddController<object>("GET", "/long", null, (_, context) =>
               {
                   context.Response.Headers[HeaderNames.ContentLength] = "2";
                   return ValueTask.FromResult<IActionResult>(new TextResult("body"));
               })
               .AddHttp3(options =>
               {
                   options.EndPoint    = new IPEndPoint(IPAddress.Loopback, port);
                   options.Certificate = certificate;
               });
        using var host = builder.Build();
        await host.StartAsync(timeout.Token);
        try
        {
            await using var connection = await QuicConnection.ConnectAsync(new QuicClientConnectionOptions
            {
                RemoteEndPoint                  = new IPEndPoint(IPAddress.Loopback, port),
                DefaultCloseErrorCode           = Http3Error.NoError,
                DefaultStreamErrorCode          = Http3Error.RequestCancelled,
                MaxInboundUnidirectionalStreams = 3,
                ClientAuthenticationOptions = new SslClientAuthenticationOptions
                {
                    TargetHost                          = "localhost",
                    ApplicationProtocols                = [new SslApplicationProtocol("h3")],
                    RemoteCertificateValidationCallback = (_, _, _, _) => true,
                },
            }, timeout.Token);
            await using var control = await connection.OpenOutboundStreamAsync(QuicStreamType.Unidirectional, timeout.Token);
            await control.WriteAsync(Frames.Integer(0), timeout.Token);
            await Frames.WriteAsync(control, Frames.Settings, ReadOnlyMemory<byte>.Empty, timeout.Token);
            await using var invalid = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, timeout.Token);
            var             fields  = RequestFields(path).ToList();
            if (path == "/bad-request")
            {
                fields.Add(("x-bad", "value\r\ninjection"));
            }

            await Frames.WriteAsync(invalid, Frames.Headers, HardeningTests.FieldSection(fields.ToArray()), timeout.Token);
            invalid.CompleteWrites();
            var error = await Assert.ThrowsAsync<QuicException>(() => invalid.CopyToAsync(Stream.Null, timeout.Token));
            Assert.Equal(QuicError.StreamAborted, error.QuicError);
            Assert.Equal(Http3Error.Message, error.ApplicationErrorCode);

            await using var healthy = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, timeout.Token);
            await Frames.WriteAsync(healthy, Frames.Headers, HardeningTests.FieldSection(RequestFields("/ok")), timeout.Token);
            healthy.CompleteWrites();
            using var body = new MemoryStream();
            while (await Frames.ReadAsync(healthy, timeout.Token) is { } frame)
            {
                var payload = await Frames.ReadPayloadAsync(healthy, frame.Length, 4096, timeout.Token);
                if (frame.Type == Frames.Headers)
                {
                    Assert.Contains((":status", "200"), QPack.Decode(payload, 4096));
                }

                if (frame.Type == Frames.Data)
                {
                    body.Write(payload);
                }
            }

            Assert.Equal("healthy", Encoding.UTF8.GetString(body.ToArray()));

            await using var head       = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, timeout.Token);
            var             headFields = RequestFields("/long").Select(field => field.Name == ":method" ? (field.Name, "HEAD") : field).ToArray();
            await Frames.WriteAsync(head, Frames.Headers, HardeningTests.FieldSection(headFields), timeout.Token);
            head.CompleteWrites();
            var headFrame = (await Frames.ReadAsync(head, timeout.Token))!.Value;
            Assert.Equal(Frames.Headers, headFrame.Type);
            var headHeaders = QPack.Decode(await Frames.ReadPayloadAsync(head, headFrame.Length, 4096, timeout.Token), 4096);
            Assert.Contains(("content-length", "2"), headHeaders);
            Assert.Null(await Frames.ReadAsync(head, timeout.Token));
        }
        finally
        {
            using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await host.StopAsync(shutdown.Token);
        }
    }

    [QuicTheory]
    [InlineData("GET", false)]
    [InlineData("HEAD", false)]
    [InlineData("GET", true)]
    public async Task EarlyHintsPrecedeTheFinalResponseWithoutEndingTheStream(string method, bool fail)
    {
        using var timeout     = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var certificate = DevelopmentCertificate.Create();
        using var reservation = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        reservation.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)reservation.LocalEndPoint!).Port;
        reservation.Close();
        var endpoint = new EarlyHintsEndpoint();
        var builder  = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddServal()
               .AddController<object>("GET", "/early-hints", null, (_, context) => endpoint.InvokeAsync(context))
               .AddHttp3(options =>
               {
                   options.EndPoint    = new IPEndPoint(IPAddress.Loopback, port);
                   options.Certificate = certificate;
               });
        using var host = builder.Build();
        await host.StartAsync(timeout.Token);
        try
        {
            await using var connection = await QuicConnection.ConnectAsync(new QuicClientConnectionOptions
            {
                RemoteEndPoint                  = new IPEndPoint(IPAddress.Loopback, port),
                DefaultCloseErrorCode           = Http3Error.NoError,
                DefaultStreamErrorCode          = Http3Error.RequestCancelled,
                MaxInboundUnidirectionalStreams = 3,
                ClientAuthenticationOptions = new SslClientAuthenticationOptions
                {
                    TargetHost                          = "localhost",
                    ApplicationProtocols                = [new SslApplicationProtocol("h3")],
                    RemoteCertificateValidationCallback = (_, _, _, _) => true,
                },
            }, timeout.Token);
            await using var control = await connection.OpenOutboundStreamAsync(QuicStreamType.Unidirectional, timeout.Token);
            await control.WriteAsync(Frames.Integer(0), timeout.Token);
            await Frames.WriteAsync(control, Frames.Settings, ReadOnlyMemory<byte>.Empty, timeout.Token);
            await using var stream = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, timeout.Token);
            var fields = RequestFields(fail ? "/early-hints?fail=true" : "/early-hints")
                         .Select(field => field.Name == ":method" ? (field.Name, method) : field).ToArray();
            await Frames.WriteAsync(stream, Frames.Headers, HardeningTests.FieldSection(fields), timeout.Token);
            stream.CompleteWrites();

            for (var index = 0; index < 2; index++)
            {
                var hint = (await Frames.ReadAsync(stream, timeout.Token))!.Value;
                Assert.Equal(Frames.Headers, hint.Type);
                var hints = QPack.Decode(await Frames.ReadPayloadAsync(stream, hint.Length, 4096, timeout.Token), 4096);
                Assert.Contains((":status", "103"), hints);
                Assert.DoesNotContain(hints, field => field.Name is "x-final" or "content-length");
                Assert.Equal(index == 0 ? [EarlyHintsEndpoint.Stylesheet, EarlyHintsEndpoint.Script] : new[] { EarlyHintsEndpoint.Font },
                    hints.Where(field => field.Name == "link").Select(field => field.Value));
            }

            endpoint.Finish.SetResult();
            var final = (await Frames.ReadAsync(stream, timeout.Token))!.Value;
            Assert.Equal(Frames.Headers, final.Type);
            var finalFields = QPack.Decode(await Frames.ReadPayloadAsync(stream, final.Length, 4096, timeout.Token), 4096);
            Assert.Contains((":status", fail ? "422" : "200"), finalFields);
            Assert.Equal(!fail, finalFields.Contains(("x-final", "only")));
            Assert.DoesNotContain(finalFields, field => field.Name == "link");
            using var body = new MemoryStream();
            while (await Frames.ReadAsync(stream, timeout.Token) is { } frame)
            {
                Assert.Equal(Frames.Data, frame.Type);
                body.Write(await Frames.ReadPayloadAsync(stream, frame.Length, 4096, timeout.Token));
            }

            if (method == "HEAD")
            {
                Assert.Empty(body.ToArray());
            }
            else
            {
                Assert.Contains(fail ? "hinted failure" : "complete", Encoding.UTF8.GetString(body.ToArray()));
            }
        }
        finally
        {
            endpoint.Finish.TrySetResult();
            using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await host.StopAsync(shutdown.Token);
        }
    }

    private static (string Name, string Value)[] RequestFields(string path) => [(":method", "GET"), (":scheme", "https"), (":authority", "localhost"), (":path", path)];

    private sealed class QuicTheoryAttribute : TheoryAttribute
    {
        public QuicTheoryAttribute()
        {
            if (!QuicListener.IsSupported)
            {
                Skip = "System.Net.Quic is unavailable on this host.";
            }
        }
    }
}
