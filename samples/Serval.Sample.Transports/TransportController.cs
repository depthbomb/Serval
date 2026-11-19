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
                                                                                    One route pipeline, Kestrel or HttpListener
                                                                                    Kestrel serves HTTP/1.1 on 127.0.0.1:8110 (override with --port).
                                                                                    HTTPS supports HTTP/1.1 and HTTP/2 on 127.0.0.1:8443 (override with --https-port).
                                                                                    Add --http3 to also enable HTTP/3 on the HTTPS port.
                                                                                    HTTPS uses a temporary self-signed development certificate.
                                                                                    HTTP/3 needs System.Net.Quic support and a compatible QUIC client.
                                                                                    Use --http-listener for the simpler HTTP/1.1 alternative on localhost:8110.
                                                                                    GET /protocol reports the negotiated protocol version.
                                                                                    GET /api/widgets/42, POST /api/widgets with JSON {"name":"Widget"}
                                                                                    GET /api/cookie, /api/download, /api/events, and /hello.json
                                                                                    GET /api/socket supports HTTP/1.1 WebSockets on either transport.
                                                                                    The focused Streaming sample includes bounded WebSocket messages and SSE resume.
                                                                                    """);

    [Get("/protocol")]
    public ValueTask<IActionResult> ProtocolAsync(RequestContext context)
        => JsonAsync(new
        {
            protocol = context.Request.ProtocolVersion.ToString(),
            scheme   = context.Request.Url.Scheme,
        });
}
