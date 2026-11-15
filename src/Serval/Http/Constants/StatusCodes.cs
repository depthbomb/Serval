#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

namespace Serval.Http.Constants;

/// <summary>HTTP status constants used by the framework and applications.</summary>
public static class StatusCodes
{
    /// <summary>HTTP 100 (Continue).</summary>
    public const int Continue = 100;

    /// <summary>HTTP 101 (Switching Protocols).</summary>
    public const int SwitchingProtocols = 101;

    /// <summary>HTTP 103 (Early Hints).</summary>
    public const int EarlyHints = 103;

    /// <summary>HTTP 200 (Ok).</summary>
    public const int Ok = 200;

    /// <summary>HTTP 201 (Created).</summary>
    public const int Created = 201;

    /// <summary>HTTP 202 (Accepted).</summary>
    public const int Accepted = 202;

    /// <summary>HTTP 204 (No Content).</summary>
    public const int NoContent = 204;

    /// <summary>HTTP 206 (Partial Content).</summary>
    public const int PartialContent = 206;

    /// <summary>HTTP 300 (Multiple Choices).</summary>
    public const int MultipleChoices = 300;

    /// <summary>HTTP 301 (Moved Permanently).</summary>
    public const int MovedPermanently = 301;

    /// <summary>HTTP 302 (Found).</summary>
    public const int Found = 302;

    /// <summary>HTTP 303 (See Other).</summary>
    public const int SeeOther = 303;

    /// <summary>HTTP 304 (Not Modified).</summary>
    public const int NotModified = 304;

    /// <summary>HTTP 307 (Temporary Redirect).</summary>
    public const int TemporaryRedirect = 307;

    /// <summary>HTTP 308 (Permanent Redirect).</summary>
    public const int PermanentRedirect = 308;

    /// <summary>HTTP 400 (Bad Request).</summary>
    public const int BadRequest = 400;

    /// <summary>HTTP 401 (Unauthorized).</summary>
    public const int Unauthorized = 401;

    /// <summary>HTTP 403 (Forbidden).</summary>
    public const int Forbidden = 403;

    /// <summary>HTTP 404 (Not Found).</summary>
    public const int NotFound = 404;

    /// <summary>HTTP 405 (Method Not Allowed).</summary>
    public const int MethodNotAllowed = 405;

    /// <summary>HTTP 406 (Not Acceptable).</summary>
    public const int NotAcceptable = 406;

    /// <summary>HTTP 408 (Request Timeout).</summary>
    public const int RequestTimeout = 408;

    /// <summary>HTTP 409 (Conflict).</summary>
    public const int Conflict = 409;

    /// <summary>HTTP 410 (Gone).</summary>
    public const int Gone = 410;

    /// <summary>HTTP 411 (Length Required).</summary>
    public const int LengthRequired = 411;

    /// <summary>HTTP 412 (Precondition Failed).</summary>
    public const int PreconditionFailed = 412;

    /// <summary>HTTP 413 (Payload Too Large).</summary>
    public const int PayloadTooLarge = 413;

    /// <summary>HTTP 414 (Uri Too Long).</summary>
    public const int UriTooLong = 414;

    /// <summary>HTTP 415 (Unsupported Media Type).</summary>
    public const int UnsupportedMediaType = 415;

    /// <summary>HTTP 416 (Range Not Satisfiable).</summary>
    public const int RangeNotSatisfiable = 416;

    /// <summary>HTTP 422 (Unprocessable Content).</summary>
    public const int UnprocessableContent = 422;

    /// <summary>HTTP 429 (Too Many Requests).</summary>
    public const int TooManyRequests = 429;

    /// <summary>HTTP 431 (Request Header Fields Too Large).</summary>
    public const int RequestHeaderFieldsTooLarge = 431;

    /// <summary>HTTP 500 (Internal Server Error).</summary>
    public const int InternalServerError = 500;

    /// <summary>HTTP 501 (Not Implemented).</summary>
    public const int NotImplemented = 501;

    /// <summary>HTTP 502 (Bad Gateway).</summary>
    public const int BadGateway = 502;

    /// <summary>HTTP 503 (Service Unavailable).</summary>
    public const int ServiceUnavailable = 503;

    /// <summary>HTTP 504 (Gateway Timeout).</summary>
    public const int GatewayTimeout = 504;

    /// <summary>HTTP 505 (Http Version Not Supported).</summary>
    public const int HttpVersionNotSupported = 505;
}
