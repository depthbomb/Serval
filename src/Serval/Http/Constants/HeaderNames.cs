#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

namespace Serval.Http.Constants;

/// <summary>HTTP field names with their conventional spelling.</summary>
public static class HeaderNames
{
    /// <summary>The <c>Accept</c> HTTP header name.</summary>
    public const string Accept = "Accept";

    /// <summary>The <c>Accept-Encoding</c> HTTP header name.</summary>
    public const string AcceptEncoding = "Accept-Encoding";

    /// <summary>The client's preferred languages.</summary>
    public const string AcceptLanguage = "Accept-Language";

    /// <summary>The patch document formats a resource accepts.</summary>
    public const string AcceptPatch = "Accept-Patch";

    /// <summary>The query formats a resource accepts, encoded as a Structured Fields list.</summary>
    /// <remarks>
    ///     For example, <c>"application/json"</c>. Media type parameters use Structured Field parameters,
    ///     such as <c>"application/json";charset="UTF-8"</c>. This is not an Accept-style header value.
    /// </remarks>
    public const string AcceptQuery = "Accept-Query";

    /// <summary>The range units supported by a resource.</summary>
    public const string AcceptRanges = "Accept-Ranges";

    /// <summary>Whether a cross-origin response may be shared with credentials.</summary>
    public const string AccessControlAllowCredentials = "Access-Control-Allow-Credentials";

    /// <summary>The request headers permitted by a CORS preflight response.</summary>
    public const string AccessControlAllowHeaders = "Access-Control-Allow-Headers";

    /// <summary>The methods permitted by a CORS preflight response.</summary>
    public const string AccessControlAllowMethods = "Access-Control-Allow-Methods";

    /// <summary>The <c>Access-Control-Allow-Origin</c> HTTP header name.</summary>
    public const string AccessControlAllowOrigin = "Access-Control-Allow-Origin";

    /// <summary>The response headers exposed to cross-origin JavaScript.</summary>
    public const string AccessControlExposeHeaders = "Access-Control-Expose-Headers";

    /// <summary>How long a CORS preflight response may be cached.</summary>
    public const string AccessControlMaxAge = "Access-Control-Max-Age";

    /// <summary>The request headers proposed by a CORS preflight request.</summary>
    public const string AccessControlRequestHeaders = "Access-Control-Request-Headers";

    /// <summary>The method proposed by a CORS preflight request.</summary>
    public const string AccessControlRequestMethod = "Access-Control-Request-Method";

    /// <summary>The estimated age of a cached response in seconds.</summary>
    public const string Age = "Age";

    /// <summary>The <c>Allow</c> HTTP header name.</summary>
    public const string Allow = "Allow";

    /// <summary>The <c>Alt-Svc</c> HTTP header name.</summary>
    public const string AltSvc = "Alt-Svc";

    /// <summary>The <c>Authorization</c> HTTP header name.</summary>
    public const string Authorization = "Authorization";

    /// <summary>The <c>Cache-Control</c> HTTP header name.</summary>
    public const string CacheControl = "Cache-Control";

    /// <summary>The <c>Connection</c> HTTP header name.</summary>
    public const string Connection = "Connection";

    /// <summary>The <c>Content-Disposition</c> HTTP header name.</summary>
    public const string ContentDisposition = "Content-Disposition";

    /// <summary>The <c>Content-Encoding</c> HTTP header name.</summary>
    public const string ContentEncoding = "Content-Encoding";

    /// <summary>The intended audience's language for the representation.</summary>
    public const string ContentLanguage = "Content-Language";

    /// <summary>The <c>Content-Length</c> HTTP header name.</summary>
    public const string ContentLength = "Content-Length";

    /// <summary>The URI identifying the enclosed representation.</summary>
    public const string ContentLocation = "Content-Location";

    /// <summary>The range covered by a partial representation.</summary>
    public const string ContentRange = "Content-Range";

    /// <summary>The browser's content security policy for this response.</summary>
    public const string ContentSecurityPolicy = "Content-Security-Policy";

    /// <summary>A content security policy evaluated for reporting without enforcement.</summary>
    public const string ContentSecurityPolicyReportOnly = "Content-Security-Policy-Report-Only";

    /// <summary>The <c>Content-Type</c> HTTP header name.</summary>
    public const string ContentType = "Content-Type";

    /// <summary>The <c>Cookie</c> HTTP header name.</summary>
    public const string Cookie = "Cookie";

    /// <summary>The policy for embedding cross-origin resources.</summary>
    public const string CrossOriginEmbedderPolicy = "Cross-Origin-Embedder-Policy";

    /// <summary>The policy for sharing a browsing context group with other documents.</summary>
    public const string CrossOriginOpenerPolicy = "Cross-Origin-Opener-Policy";

    /// <summary>The policy for loading this resource from another origin.</summary>
    public const string CrossOriginResourcePolicy = "Cross-Origin-Resource-Policy";

    /// <summary>The legacy Do Not Track preference; clients and servers may ignore it.</summary>
    public const string Dnt = "DNT";

    /// <summary>The <c>Date</c> HTTP header name.</summary>
    public const string Date = "Date";

    /// <summary>The <c>ETag</c> HTTP header name.</summary>
    public const string ETag = "ETag";

    /// <summary>Whether a request was forwarded using TLS early data.</summary>
    public const string EarlyData = "Early-Data";

    /// <summary>The client's expectations, commonly <c>100-continue</c>.</summary>
    public const string Expect = "Expect";

    /// <summary>The time after which a response is considered stale.</summary>
    public const string Expires = "Expires";

    /// <summary>Proxy forwarding information; only trust values from known proxies.</summary>
    public const string Forwarded = "Forwarded";

    /// <summary>The <c>Host</c> HTTP header name.</summary>
    public const string Host = "Host";

    /// <summary>The entity tags required for a conditional request to proceed.</summary>
    public const string IfMatch = "If-Match";

    /// <summary>The <c>If-Modified-Since</c> HTTP header name.</summary>
    public const string IfModifiedSince = "If-Modified-Since";

    /// <summary>The <c>If-None-Match</c> HTTP header name.</summary>
    public const string IfNoneMatch = "If-None-Match";

    /// <summary>The validator controlling whether a range request returns part or all of a representation.</summary>
    public const string IfRange = "If-Range";

    /// <summary>The modification time required for a conditional request to proceed.</summary>
    public const string IfUnmodifiedSince = "If-Unmodified-Since";

    /// <summary>Legacy HTTP/1.x connection parameters; forbidden in HTTP/2 and HTTP/3.</summary>
    public const string KeepAlive = "Keep-Alive";

    /// <summary>The <c>Last-Event-ID</c> HTTP header name.</summary>
    public const string LastEventId = "Last-Event-ID";

    /// <summary>The <c>Last-Modified</c> HTTP header name.</summary>
    public const string LastModified = "Last-Modified";

    /// <summary>The <c>Link</c> HTTP header name.</summary>
    public const string Link = "Link";

    /// <summary>The <c>Location</c> HTTP header name.</summary>
    public const string Location = "Location";

    /// <summary>The <c>Origin</c> HTTP header name.</summary>
    public const string Origin = "Origin";

    /// <summary>The browser features available to this document and its embedded content.</summary>
    public const string PermissionsPolicy = "Permissions-Policy";

    /// <summary>Legacy HTTP caching directives.</summary>
    public const string Pragma = "Pragma";

    /// <summary>An authentication challenge issued by a proxy.</summary>
    public const string ProxyAuthenticate = "Proxy-Authenticate";

    /// <summary>Credentials for authenticating with a proxy.</summary>
    public const string ProxyAuthorization = "Proxy-Authorization";

    /// <summary>A nonstandard legacy proxy connection field; forbidden in HTTP/2 and HTTP/3.</summary>
    public const string ProxyConnection = "Proxy-Connection";

    /// <summary>The requested ranges of a representation.</summary>
    public const string Range = "Range";

    /// <summary>The referring resource URI, using the HTTP field's historical spelling.</summary>
    public const string Referer = "Referer";

    /// <summary>The policy controlling which referrer information the browser sends.</summary>
    public const string ReferrerPolicy = "Referrer-Policy";

    /// <summary>The <c>Retry-After</c> HTTP header name.</summary>
    public const string RetryAfter = "Retry-After";

    /// <summary>The browser's request destination, such as document or image.</summary>
    public const string SecFetchDest = "Sec-Fetch-Dest";

    /// <summary>The browser's request mode, such as navigate or cors.</summary>
    public const string SecFetchMode = "Sec-Fetch-Mode";

    /// <summary>The relationship between the request initiator's site and the destination.</summary>
    public const string SecFetchSite = "Sec-Fetch-Site";

    /// <summary>Whether a navigation request was triggered by user activation.</summary>
    public const string SecFetchUser = "Sec-Fetch-User";

    /// <summary>The server's WebSocket handshake response.</summary>
    public const string SecWebSocketAccept = "Sec-WebSocket-Accept";

    /// <summary>The proposed or negotiated WebSocket extensions.</summary>
    public const string SecWebSocketExtensions = "Sec-WebSocket-Extensions";

    /// <summary>The client's WebSocket handshake nonce.</summary>
    public const string SecWebSocketKey = "Sec-WebSocket-Key";

    /// <summary>The proposed or selected WebSocket subprotocol.</summary>
    public const string SecWebSocketProtocol = "Sec-WebSocket-Protocol";

    /// <summary>The proposed or supported WebSocket protocol version.</summary>
    public const string SecWebSocketVersion = "Sec-WebSocket-Version";

    /// <summary>The <c>Server</c> HTTP header name.</summary>
    public const string Server = "Server";

    /// <summary>Server timing metrics exposed with the response.</summary>
    public const string ServerTiming = "Server-Timing";

    /// <summary>The <c>Set-Cookie</c> HTTP header name.</summary>
    public const string SetCookie = "Set-Cookie";

    /// <summary>The policy requiring future HTTPS connections to this host.</summary>
    public const string StrictTransportSecurity = "Strict-Transport-Security";

    /// <summary>The accepted transfer codings; HTTP/2 and HTTP/3 allow only <c>trailers</c>.</summary>
    public const string Te = "TE";

    /// <summary>The origins allowed to read detailed resource timing information.</summary>
    public const string TimingAllowOrigin = "Timing-Allow-Origin";

    /// <summary>The W3C distributed tracing parent identifier.</summary>
    public const string TraceParent = "traceparent";

    /// <summary>Vendor-specific W3C distributed tracing state.</summary>
    public const string TraceState = "tracestate";

    /// <summary>The field names expected in the message's trailer section.</summary>
    public const string Trailer = "Trailer";

    /// <summary>The <c>Transfer-Encoding</c> HTTP header name.</summary>
    public const string TransferEncoding = "Transfer-Encoding";

    /// <summary>The <c>Upgrade</c> HTTP header name.</summary>
    public const string Upgrade = "Upgrade";

    /// <summary>The <c>User-Agent</c> HTTP header name.</summary>
    public const string UserAgent = "User-Agent";

    /// <summary>The <c>Vary</c> HTTP header name.</summary>
    public const string Vary = "Vary";

    /// <summary>Information about intermediaries that forwarded the message.</summary>
    public const string Via = "Via";

    /// <summary>The <c>WWW-Authenticate</c> HTTP header name.</summary>
    public const string WwwAuthenticate = "WWW-Authenticate";

    /// <summary>The <c>X-Content-Type-Options</c> HTTP header name.</summary>
    public const string XContentTypeOptions = "X-Content-Type-Options";

    /// <summary>The conventional CSRF token field used by Serval cookie authentication.</summary>
    public const string XCsrfToken = "X-CSRF-Token";

    /// <summary>A conventional forwarded client address list; only trust known proxies.</summary>
    public const string XForwardedFor = "X-Forwarded-For";

    /// <summary>The original Host field recorded by a proxy; only trust known proxies.</summary>
    public const string XForwardedHost = "X-Forwarded-Host";

    /// <summary>The original request scheme recorded by a proxy; only trust known proxies.</summary>
    public const string XForwardedProto = "X-Forwarded-Proto";

    /// <summary>The legacy policy controlling whether a page can be displayed in a frame.</summary>
    public const string XFrameOptions = "X-Frame-Options";

    /// <summary>The legacy accepted character sets field.</summary>
    public const string AcceptCharset = "Accept-Charset";

    /// <summary>The MIME transfer encoding of a multipart part; Serval rejects encoded form parts.</summary>
    public const string ContentTransferEncoding = "Content-Transfer-Encoding";

    /// <summary>The maximum number of forwarding hops for TRACE or OPTIONS.</summary>
    public const string MaxForwards = "Max-Forwards";

    /// <summary>The legacy timed navigation field.</summary>
    public const string Refresh = "Refresh";

    /// <summary>The obsolete HTTP cache warning field, retained for protocol compatibility.</summary>
    public const string Warning = "Warning";

    /// <summary>The obsolete Certificate Transparency policy field, retained for protocol compatibility.</summary>
    public const string ExpectCt = "Expect-CT";

    /// <summary>The legacy request purpose field, such as prefetch.</summary>
    public const string Purpose = "Purpose";

    /// <summary>The client's preference for an encrypted and authenticated response.</summary>
    public const string UpgradeInsecureRequests = "Upgrade-Insecure-Requests";

    /// <summary>The legacy browser XSS filter control; modern applications should use Content-Security-Policy.</summary>
    public const string XXssProtection = "X-XSS-Protection";

    /// <summary>Distributed tracing application context.</summary>
    public const string Baggage = "baggage";

    /// <summary>The HTTP extensible priority parameters.</summary>
    public const string Priority = "Priority";

    /// <summary>The browser data to clear for this origin.</summary>
    public const string ClearSiteData = "Clear-Site-Data";

    /// <summary>A digest of the HTTP message content.</summary>
    public const string ContentDigest = "Content-Digest";

    /// <summary>A digest of the selected representation.</summary>
    public const string ReprDigest = "Repr-Digest";

    /// <summary>The original port recorded by a proxy; only trust known proxies.</summary>
    public const string XForwardedPort = "X-Forwarded-Port";

    /// <summary>A conventional client identification field, commonly XMLHttpRequest.</summary>
    public const string XRequestedWith = "X-Requested-With";
}
