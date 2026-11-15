#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Serval.Http;
using Serval.Routing;

namespace Serval.Sample.Transports;

[Controller]
public sealed class TransportController : ControllerBase
{
    [Get("/")]
    public ValueTask<IActionResult> IndexAsync(RequestContext context) => TextAsync("""
                                                                                    One route pipeline, three transports
                                                                                    HTTP/1.1 is enabled by default on localhost:8110 (override with --port).
                                                                                    Add --http2 for TLS on 127.0.0.1:8444 (override with --http2-port).
                                                                                    Add --http3 for QUIC on 127.0.0.1:8443 (override with --http3-port).
                                                                                    HTTP/2 and HTTP/3 use a temporary self-signed development certificate.
                                                                                    HTTP/3 needs System.Net.Quic support and a compatible QUIC client.
                                                                                    GET /protocol reports the negotiated protocol version.
                                                                                    GET /api/widgets/42, POST /api/widgets with JSON {"name":"Widget"}
                                                                                    GET /api/cookie, /api/download, /api/events, and /hello.json
                                                                                    GET /api/socket supports WebSockets on HttpListener only.
                                                                                    The focused Streaming sample includes bounded WebSocket messages and SSE resume.
                                                                                    """);

    [Get("/protocol")]
    public ValueTask<IActionResult> ProtocolAsync(RequestContext context)
        => JsonAsync(new { protocol = context.Request.ProtocolVersion.ToString(), scheme = context.Request.Url.Scheme });
}
