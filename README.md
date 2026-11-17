# Serval

Serval is an async, controller-based web framework for .NET. It builds on `Microsoft.Extensions.Hosting`, so HTTP
endpoints share configuration, dependency injection, logging, and background services with the rest of your app.

Start with the core and a transport, then add authentication, templates, or frontend assets as you need them. This is
still an internal, pre-1.0 project, so APIs and template syntax may change.

## What it covers

- Controllers with generated registration, typed parameter binding, route constraints, and URL helpers.
- URL-encoded and multipart forms, uploaded files, and shared form/JSON POST-value helpers.
- Request-scoped services, composable middleware, cancellation, configurable limits, and error handling.
- Text, JSON, files, redirects, static-file serving, and an optional `index.html` fallback for client-side routes.
- Streaming responses, server-sent events, and transport-specific features such as WebSockets and Early Hints.
- Cookie and bearer authentication, authorization policies, password hashing, CSRF helpers, and OAuth/OIDC sign-in.
- Signed route URLs with readable, authenticated route and query data and optional expiration.
- Compiled Savanna templates and generated Vite asset references, with Rider integration and frontend HMR support.

`RequestContext` carries request state, services, and endpoint metadata. Optional `ControllerBase` provides result,
input, and HTTP-error conveniences. Configure an instance through `AddServal(options => ...)`, including routing
behavior, JSON settings,
static files, timeouts, and trace ID generation.

## Packages

These ten projects produce NuGet packages. Build them locally or consume them through your internal feed.

| Package                                                            | Purpose                                                                                |
|--------------------------------------------------------------------|----------------------------------------------------------------------------------------|
| [Serval](src/Serval)                                               | Hosting, controllers, routing, middleware, HTTP helpers, and static files.             |
| [Serval.Authentication](src/Serval.Authentication)                 | Authentication, sessions, authorization, password hashing, and CSRF helpers.           |
| [Serval.OAuth](src/Serval.OAuth)                                   | OAuth 2.0 clients and OIDC sign-in with discovery, PKCE, and validated ID tokens.       |
| [Serval.SignedUrls](src/Serval.SignedUrls)                         | Signed route URLs, verified payload data, request validation, and optional expiration. |
| [Serval.SocketIO](src/Serval.SocketIO)                             | Socket.IO events, binary payloads, acknowledgements, namespaces, and in-memory rooms.  |
| [Serval.Savanna](src/Serval.Savanna)                               | Compiled HTML templates with typed models, layouts, includes, and components.          |
| [Serval.Vite](src/Serval.Vite)                                     | Asset lookups generated from Vite manifests, plus development URLs for HMR.            |
| [Serval.Transport.HttpListener](src/Serval.Transport.HttpListener) | HTTP and WebSockets through `System.Net.HttpListener`; the default in most samples.    |
| [Serval.Transport.Http2](src/Serval.Transport.Http2)               | Experimental HTTP/2 transport using TCP and TLS.                                       |
| [Serval.Transport.Http3](src/Serval.Transport.Http3)               | Experimental HTTP/3 transport using QUIC.                                              |

Packages include their source generators where needed. Repository project references wire those analyzers up explicitly;
the [routing sample project](samples/Serval.Sample.Routing/Serval.Sample.Routing.csproj) shows that setup.

Namespaces follow the project folders. HTTP constants such as `StatusCodes` live in `Serval.Http.Constants`, static-file
options in `Serval.Http.StaticFiles`, and authorization policies, attributes, and helpers in
`Serval.Authentication.Authorization`. Import the namespaces you use alongside the package's root namespace.

## Try it out

Use the .NET SDK selected by [global.json](global.json). The framework targets .NET 11 and C# 15. From the repository
root:

```powershell
dotnet run --project samples/Serval.Sample.Routing
```

Open <http://localhost:8101/> for a list of routes to try. Add `-- --port 8201` to use a different port.

For your own app, reference `Serval` and a transport. With `Serval.Transport.HttpListener` in a .NET 11 console project
with implicit usings enabled, a small application looks like this:

```csharp
using Microsoft.Extensions.Hosting;
using Serval;
using Serval.Generated;
using Serval.Hosting;
using Serval.Http;
using Serval.Routing;
using Serval.Transport.HttpListener;

public static class Program
{
    public static async Task Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.Services.AddServal().AddGeneratedControllers().AddHttpListener();

        using var host = builder.Build();
        await host.RunAsync();
    }
}

[Controller]
public sealed class HomeController : ControllerBase
{
    [Get("/")]
    public ValueTask<IActionResult> IndexAsync() => TextAsync("Hello from Serval!");
}
```

This listens on <http://localhost:8080/>. Controllers support constructor injection, and actions return
`Task<IActionResult>` or `ValueTask<IActionResult>`. The samples below cover bindings, configuration, and other common
application patterns in more detail.

`AddGeneratedControllers()` registers controllers from the current assembly. For a referenced assembly named
`MyLibrary`, call `Serval.Generated.MyLibraryControllers.Register(builder)` with your `ServalBuilder`.

## Lifecycle handlers and middleware

Choose based on when the work belongs in a request:

| Work                                                                  | Use                                              |
|-----------------------------------------------------------------------|--------------------------------------------------|
| Initialize request-scoped state before dispatch                       | `OnRequestReceivedAsync` in a lifecycle handler  |
| Set final response headers, including error responses                 | `OnResponseStartingAsync` in a lifecycle handler |
| Record the final status, duration, failure, or abort                  | `OnRequestCompletedAsync` in a lifecycle handler |
| Authenticate, authorize, serve a cache hit, or reject a request early | Middleware                                       |
| Wrap downstream work with timing, a transaction, or `try`/`finally`   | Middleware                                       |
| Add a callback needed only by one action or one request               | `Response.OnStarting` / `Response.OnCompleted`   |

Lifecycle handlers run at fixed phases. They have no `next` delegate and do not decide whether routing or the next
middleware runs. Middleware controls downstream execution by awaiting `next(context)` or returning its own response.
For example, maintenance mode belongs in middleware; logging whether that maintenance response finished belongs in
a lifecycle completion hook. Timing just an action's downstream pipeline also belongs in middleware, while recording
the final request outcome belongs in a completion hook.

### Request lifecycle handlers

Derive from `Serval.Lifecycle.RequestLifecycleHandler` and override the hooks you need. Register handlers at startup:

```csharp
var serval = builder.Services.AddServal();
serval.AddRequestLifecycleHandler<RequestTimingHandler>(priority: -100);
serval.AddRequestLifecycleHandler<ResponseHeadersHandler>();
serval.AddRequestLifecycleHandler<RequestAuditHandler>(priority: 100);
serval.AddGeneratedControllers().AddHttpListener();
```

Each handler type is registered once as a scoped service. Its constructor can take scoped dependencies, and the same
instance serves every phase of one request. Handlers run sequentially in ascending priority order in every phase.
Negative values are supported; the default is `0`, and ties preserve registration order. Registration closes when
the host starts. Registration through a route group still applies to the whole application.

| Hook                                                                            | Timing and available state                                                                                                                                                                            |
|---------------------------------------------------------------------------------|-------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| `OnRequestReceivedAsync(RequestContext, CancellationToken)`                     | The parsed request and scope exist. Application middleware, authentication, forwarded-header processing, and endpoint selection have not run. Initialize state here; do not send or abort a response. |
| `OnResponseStartingAsync(RequestContext, CancellationToken)`                    | Local `OnStarting` callbacks have run, and final headers are still mutable. Set headers or status here; do not write a body or start the response.                                                    |
| `OnRequestCompletedAsync(RequestContext, RequestCompletion, CancellationToken)` | Response processing and local `OnCompleted` callbacks have ended. The response is unwritable, but scoped services and uploads are still available.                                                    |

Hooks cover controllers, static files, redirects, automatic OPTIONS, missing routes, and error responses. Transports
can reject malformed requests before a request context exists; those requests do not reach these hooks. A starting
hook also runs before a WebSocket upgrade. Completion follows the end of the request's processing, including an
awaited streaming or WebSocket action, and does not guarantee client receipt of the response.

`RequestCompletion` captures `StatusCode`, `HasStarted`, `IsAborted`, `Elapsed`, `Reason`, and the original `Exception`
before completion callbacks start. Reasons are `Completed`, `Canceled`, `TimedOut`, `TransportError`, and `Aborted`.
A successfully written 500 has reason `Completed` and retains its application exception. A failure after a 200 has
started can have reason `Aborted`. `Canceled` covers the transport's cancellation signal, including disconnects and
shutdown; `TimedOut` identifies the framework's request deadline. Elapsed time excludes completion callbacks and
cleanup.

The [Lifecycles sample](samples/Serval.Sample.Lifecycles) demonstrates scoped initialization, response headers, final
outcome logging, and local callbacks. It includes successful requests, files, redirects, errors, timeouts, and aborts.

### Callback order, failures, and cleanup

Local response callbacks keep their reverse registration order. At response startup, local `OnStarting` callbacks
run first, followed by lifecycle starting hooks in ascending priority order. This lets lifecycle handlers apply final
header policies after action-specific callbacks. Middleware may still be executing when the response starts.

At the end of processing, the order is local `OnCompleted` callbacks, lifecycle completion hooks, resources registered
with `Response.RegisterForDisposeAsync`, request-scope disposal, and upload/body cleanup. Registered resources are
disposed in reverse registration order. Only transfer ownership of resources that dependency injection does not already
own.

Receiving and starting failures go through the framework's exception handler. Receiving hooks stop at the first
failure; already resolved handlers still participate in response startup and completion. Each starting callback or
hook runs at most once. If it throws, uncalled starting callbacks and hooks can run while the error response starts.
If that error response also fails, the response is aborted. Completion and registered-resource cleanup failures are
logged independently, and the remaining callbacks, hooks, and cleanup still run.

`Response.OnStarting` and `Response.OnCompleted` retain their parameterless `Func<ValueTask>` overloads and also accept
`Func<CancellationToken, ValueTask>`. Starting receives the token used for that response, which can differ from the
canceled request token during error handling. Completion callbacks and hooks share a separate cooperative budget,
configured by `ServalOptions.CompletionTimeout` (five seconds by default, or `Timeout.InfiniteTimeSpan` to disable it).
All completion callbacks and hooks are invoked even after that token is canceled. They must observe the token to
stop promptly; the framework awaits them before disposing their dependencies. Resource disposal is always awaited.
Queue long-running jobs to background services using independent data, without capturing request-scoped services.

Middleware implementing `IAsyncDisposable` stays alive through completion callbacks and hooks, then is disposed before
the request scope. Put work that must run while the pipeline unwinds in `InvokeAsync`'s `finally` block.

### Application middleware

`ServalBuilder.Use` surrounds all framework dispatch, including static files, redirects, automatic OPTIONS, and
unmatched routes. Middleware enters in registration order. Await `next(context)` to continue, or write a response
to short-circuit. `UseMiddleware<T>()` supports constructor injection and asynchronous disposal after request completion
hooks.

```csharp
var maintenanceEnabled = builder.Configuration.GetValue<bool>("maintenance");
builder.Services.AddServal().Use(async (context, next) =>
{
    if (maintenanceEnabled)
    {
        context.Response.StatusCode = StatusCodes.ServiceUnavailable;
        context.Response.Headers["Retry-After"] = "60";
        await context.Response.WriteTextAsync("Maintenance is enabled.", context.RequestAborted);
        return;
    }

    await next(context);
}).AddGeneratedControllers().AddHttpListener();
```

Endpoint middleware attributes surround matched controller actions and their results. Their priorities run in ascending
order, with ties preserving registration order. Code after `await next(context)` runs as the pipeline unwinds; use
`finally` for work that also needs to run on exceptions. The response may already have started by then. Framework
exception handling surrounds the application pipeline, so use lifecycle completion hooks for the final response outcome.

The [middleware sample](samples/Serval.Sample.Middleware) measures downstream application and route execution and
demonstrates maintenance short-circuiting. Try `/work`, `/crash`, and `/missing`, then restart with `--maintenance true`
to see the inner middleware and controller being skipped while outer middleware still unwinds.

## Endpoint policies and groups

`ConfigureEndpoints` runs at startup for controllers registered through that builder, including generated registrations.
Inspect `Method`, `Template`, `Name`, or `ControllerType` to select endpoints. Set `RequestTimeout`,
`MaxRequestBodyBytes`, or `Forms`, and add shared middleware or application metadata:

```csharp
serval.AddGroup("/api", group =>
{
    group.ConfigureEndpoints(endpoint =>
    {
        endpoint.MaxRequestBodyBytes = 32 * 1024;
        endpoint.RequestTimeout = TimeSpan.FromSeconds(10);
        endpoint.Middleware.Add(new AuthorizeAttribute());
    });
    group.AddGeneratedControllers();
});
```

Groups can nest and can take a `namePrefix` for named-route URL generation. Parent conventions run before child
conventions, even when configured after controller registration. Middleware priorities still determine execution order.
The selected `context.Endpoint` exposes its method, full template, name, controller type, and `GetMetadata<T>()`.
It is null for static files, unmatched requests, redirects, and automatic OPTIONS. Application middleware can inspect it
after `next` returns; endpoint middleware can inspect it before calling `next`.

Set the application body ceiling high enough for your largest endpoint, then apply smaller limits to other endpoints.
Endpoint limits cannot exceed that ceiling, which transports also enforce. Configure form limits through
`endpoint.Forms`; settings are copied at startup. Read bodies after endpoint selection when using custom endpoint
limits.
Endpoint deadlines are measured from request arrival, apply when a route is selected, and cannot undo cancellation that
already occurred. `Timeout.InfiniteTimeSpan` disables the endpoint deadline while preserving disconnect cancellation.

## Proxies and diagnostics

`Request.Connection` contains the transport's remote and local addresses and ports. Register trusted-proxy middleware
before application middleware that uses client identity or public URLs:

```csharp
serval.UseForwardedHeaders(options =>
{
    options.KnownProxies.Add(IPAddress.Parse("10.0.0.10"));
    options.AllowedHosts.Add("app.example.com");
    options.ForwardLimit = 1;
});
```

Import `System.Net` for addresses and networks. No proxies are trusted by default. `KnownNetworks` accepts `IPNetwork`
values when a deployment needs subnet-based trust. The middleware consumes `X-Forwarded-For` from right to left and
stops at an untrusted hop or `ForwardLimit`. Optional `X-Forwarded-Proto`, `X-Forwarded-Host`, and `X-Forwarded-Prefix`
lists must describe the same number of hops. It requires `X-Forwarded-For`; the standardized `Forwarded` header is not
interpreted. Invalid consumed values return 400. Configuration is copied when registered.

After processing, `ClientAddress`, `Scheme`, `Host`, `PathBase`, and `PublicUrl` expose the effective public request.
`Connection` and `Url` keep the original transport values. The proxy should strip its external path prefix before
forwarding; routing and signed URL validation continue to use that application-relative path. Route URL generators
also return application-relative paths; prepend `Request.PathBase` when publishing links under an external mount.

Subscribe to the `Serval` ActivitySource and Meter, identified by `ServalDiagnostics.InstrumentationName`, using BCL
listeners or your telemetry exporter. Server activities honor valid incoming W3C `traceparent` / `tracestate` headers.
Metrics include `http.server.active_requests`, `http.server.request.duration` in seconds, and
`http.server.request.errors`.
Tags include normalized HTTP methods, final status codes, and matched route templates. Instrumentation does not record
query strings, request bodies, credentials, raw route values, or exception messages. Existing `ILogger` integration and
the configurable trace ID factory remain available.

## Request values and forms

Run `dotnet run --project samples/Serval.Sample.Forms` and open <http://localhost:8116/> to try browser forms, JSON,
and file uploads against the same endpoint. The sample also shows `[FromForm]` binding.

You can read route parameters, query strings, and submitted values without parameter attributes:

```csharp
// Inside a ControllerBase action:
var id = RouteValue<int>("id");
var page = QueryValue("page", 1);
var tags = QueryValues<string>("tag");

var post = await ReadPostAsync();
var username = post.Get<string>("username");
var enabled = post.Get("enabled", false);
var ids = post.GetValues<int>("ids");
```

`ReadPostAsync()` accepts `application/x-www-form-urlencoded`, `multipart/form-data`, `application/json`, and
`application/*+json`. It also works on PUT and PATCH requests. JSON must be an object; each top-level property is a
named value. JSON arrays and repeated form fields are available through `GetValues<T>()`. Strings, numbers, and
booleans can be read as scalars with invariant-culture parsing. For a nested JSON object or array, pass generated
metadata: `post.Get("profile", AppJsonContext.Default.Profile)`. This overload is for JSON bodies; validate the
deserialized object's members before using them.

Names ignore case. Required scalar reads reject missing, null, repeated, or invalid values with 400. A fallback
applies only when a name is absent. `TryGet<T>()`, `TryRouteValue<T>()`, and `TryQueryValue<T>()` return false for
missing
or invalid values. `GetEnum<T>()`, `RouteEnum<T>()`, and `QueryEnum<T>()` handle enums. Missing repeated values return
an empty array; submitted invalid elements still return 400. Duplicate JSON property names are rejected rather than
choosing a value silently.

For a single body field, use `PostValueAsync<T>("name")` or `PostValuesAsync<T>("name")` in a `ControllerBase` action.
Plain controllers and middleware use `context.Request` for query and body operations, `context.RouteValues` for raw
route values, and `context.Response` for writes. Plain controllers can return concrete result objects such as
`new TextResult("hello")` from `Serval.Http.Results`. Core convenience extensions on `RequestContext` have been removed;
authentication and signed-URL context operations remain available. Existing `[FromRoute]`, `[FromQuery]`, and JSON
`[FromBody]` binding still work, and inheriting `ControllerBase` is optional.

`ReadFormAsync()` accepts only the two form content types. `[FromForm]` binds scalar fields, scalar arrays,
`UploadedFile`, and `UploadedFile[]`; multiple parameters share one parse. Form and `[FromBody]` parameters cannot
be combined on an action. Application validation, such as required nonempty names, remains separate from parsing.

```csharp
var form = await context.Request.ReadFormAsync();
var attachment = form.GetFile("attachment"); // null when absent; repeated files require GetFiles()
if (attachment is not null)
{
    // Choose an application-owned destination; FileName and ContentType come from the client.
    await attachment.CopyToAsync(destinationStream, context.RequestAborted);
}
```

`form.Files` contains all uploaded files in submission order. Each file exposes its field name, client filename,
content type, actual byte length, an independent `OpenReadStream()`, and `CopyToAsync()`. Finish reading or copy files
before the request ends. The framework then closes their shared buffer and deletes any temporary file. Manually
constructed `Request` objects should be disposed with `await using`.

Parsed form/POST data is cached for repeated reads. Choose this mode before consuming the body with `ReadBodyAsync`,
`ReadTextAsync`, `ReadJsonAsync`, or `CopyBodyToAsync`; the two modes cannot be mixed. Failed or canceled parsing is
not retried against a partially consumed body. `CopyBodyToAsync(destination, ct)` streams a raw body without buffering
it in full and enforces the same request limit and cancellation rules.

The total body limit remains `MaxRequestBodyBytes`, 1 MiB by default. Configure `options.Forms` for field counts,
decoded name/value lengths, file counts and sizes, multipart headers, the memory threshold, and temporary directory.
Parsed bodies use memory up to 64 KiB by default, then spill to temporary storage. This buffers the body before
parsing; uploaded files are slices of that buffer, so their contents are not copied a second time. Defaults allow
1,024 field values, 32 files, 2,048-character names, 1 MiB text values, 128 MiB per file, and 16 headers / 16 KiB of
headers per multipart part. The total body limit always applies, even if a file limit is higher.

The readers support UTF-8 and reject malformed encoding, invalid multipart framing, and ambiguous headers with 400.
Unsupported media types or charsets return 415; configured size/count limits return 413. Cookie authentication's
`[RequireCsrf]` accepts one `_csrf` form field when its token header is absent. Set `FormFieldName` to change that name
or to `null` to require the header. A bad or repeated header never falls back to a form token.

For large uploads, `Request.ReadMultipartAsync` processes sections as bytes arrive, without a whole-body buffer or
temporary files. The Forms sample's `/stream` endpoint demonstrates this mode:

```csharp
await Request.ReadMultipartAsync(async (section, ct) =>
{
    if (section.FileName is not null)
    {
        await section.Body.CopyToAsync(destinationStream, ct);
    }
}, RequestAborted);
```

Each section exposes `Name`, `FileName`, `ContentType`, `Headers`, and an asynchronous `Body` stream. Finish reads
inside
the callback and leave that stream open. Unread bytes are drained before advancing; skipped parts still undergo limits,
UTF-8 checks for text fields, and framing validation. The total limit also covers multipart epilogues. Choose streaming
before any other body reader. A failed parse cannot be retried; application-owned destinations may contain partial data,
so publish stored files only after the whole operation succeeds.

## Input validation

Register application validators with `serval.AddInputValidator<SignupInput, SignupValidator>()`. Implement
`IInputValidator<SignupInput>` from `Serval.Validation`; validators are scoped services and can use constructor
injection.
Their `ValidateAsync(input, errors, context, ct)` method adds client-visible messages with
`errors.Add("email", "Enter a valid email address.")`. An empty field name represents an object-level error.

Generated `[FromBody]` bindings only deserialize. Use `[FromBody(Validate = true)]` to run all validators registered
for that input type before invoking the action. Existing actions that relied on automatic validation need this opt-in.
Validators run in registration order and collect all reported errors. Invalid input returns 422 with
`code: "validation_failed"` and an `errors` object mapping field names to message arrays. Malformed input still returns
the existing parsing error; unexpected validator exceptions use the ordinary exception handler.

For models assembled from form or POST fields, controllers can call `await ValidateAsync(input)`.
Plain controllers and middleware can call `InputValidation.ValidateAsync(context, input)`. With no registered
validators this adds no application rules. JSON member nullability and business rules remain explicit validation work.
You can also construct `ValidationErrors` directly and call `ThrowIfInvalid()` for one-off checks.

For an HTML form or another response policy, call `InputValidation.EvaluateAsync(context, input)`. Its immutable result
contains `IsValid` and `Errors`, so an action can redisplay submitted values or return its own response. Calling
`result.ThrowIfInvalid()` selects the usual exception-based behavior. Parsing errors and validator exceptions still
propagate; only reported field errors are returned as data.

## Composing request handling

Controllers, static files, and authentication helpers use public pieces that applications can also call directly.
The examples below belong in application methods; configure builders before starting the host.

### Select an endpoint before running it

`AddEndpoint` registers a `RequestDelegate` with groups, names, metadata, limits, and endpoint middleware. Its
`Endpoint.ControllerType` is null. Controllers remain an optional adapter that activates a scoped instance and executes
an `IActionResult`.

```csharp
public static void ConfigureRoutes(ServalBuilder builder)
{
    builder.AddEndpoint("GET", "/health", context => context.Response.WriteTextAsync("ready"), name: "health");
    builder.DispatchWith(async context =>
    {
        var matcher = context.Services.GetRequiredService<IEndpointMatcher>();
        var match = matcher.Match(context);
        if (match.Endpoint is not null)
        {
            match.Apply(context);
            // Inspect metadata or authorize here, before executing endpoint middleware and the handler.
            await match.ExecuteAsync(context);
            return;
        }

        context.Response.StatusCode = match.AllowedMethods.Count == 0 ? 404 : 405;
        if (match.AllowedMethods.Count > 0)
        {
            context.Response.Headers["Allow"] = string.Join(", ", match.AllowedMethods);
        }
    });
}
```

`Match` leaves the response and selected endpoint unchanged. `Apply` installs route values and enforces endpoint body
limits and deadlines; call it before middleware reads the body. `ExecuteAsync` also applies those policies. Application
code controls which endpoint to execute and should execute it once.

`DispatchWith` replaces the default canonical redirect, routing, static-file, and 404 sequence. Request scopes,
framing checks, cancellation, exception handling, lifecycle hooks, and cleanup remain active. For standard method
handling, use `RoutingMiddleware.TryHandleAsync(context)` instead of manually handling the match result; it runs
matching endpoints, handles automatic OPTIONS and 405, and returns false only for an unmatched path. Construct it with
`automaticOptions: false` to disable synthesized OPTIONS responses. HEAD selection follows `ServalOptions.AutomaticHead`.
`CanonicalPathMiddleware.TryRedirectAsync` provides the default slash redirect behavior as a separate step.

### Feed a custom parser from the request body

```csharp
public static async ValueTask ReadLinesAsync(RequestContext context)
{
    await using var body = context.Request.OpenBodyStream(context.RequestAborted);
    using var reader = new StreamReader(body, leaveOpen: true);
    while (await reader.ReadLineAsync(context.RequestAborted) is {} line)
    {
        // Process each line here. Add a parser-specific line limit if the body limit is too broad.
    }
}
```

The lease enforces cumulative body limits and request cancellation and leaves the transport stream open. Dispose it
before using another raw reader, which continues at the current position. Concurrent reads and mixing raw reads with
cached form data or streaming multipart are rejected. JSON, forms, and multipart use the same bounded reader.

## Signed URLs

Run the [signed URL sample](samples/Serval.Sample.SignedUrls) with
`dotnet run --project samples/Serval.Sample.SignedUrls`, then open <http://localhost:8115/>. Request `/links` for
permanent, temporary, typed-helper, and inspection links. Follow the returned paths on the same server. Use
`/links?seconds=5` to try expiration, or change a report ID or query value to see validation reject the request.
The sample generates a new key on startup unless `SignedUrls:Key` is configured, so its default links stop working
after a restart. Set `SignedUrls__Key` in the environment to use a persistent base64 signing key.

Reference `Serval.SignedUrls` and import `Serval.SignedUrls`. Register it on the Serval builder with a secret,
cryptographically random key of at least 32 bytes:

```csharp
builder.Services.AddServal().AddGeneratedControllers().AddSignedUrls(options =>
{
    options.SigningKey = Convert.FromBase64String(builder.Configuration["SignedUrls:Key"]
        ?? throw new InvalidOperationException("Configure SignedUrls:Key."));
}).AddHttpListener();
```

Generate a key once with `Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))` from
`System.Security.Cryptography`, then keep it in your application's secret configuration. Use the same key on every
instance and after restarts. Changing it invalidates outstanding URLs. Give separate applications separate keys.

Inject `ISignedUrlService` into a controller or resolve it with
`context.Services.GetRequiredService<ISignedUrlService>()`. For a route named `downloads.show` with an `{id}` parameter:

```csharp
var permanent = signedUrls.Path("downloads.show",
    values: new Dictionary<string, object?> { ["id"] = 42 },
    query: new Dictionary<string, object?> { ["format"] = "pdf" });

var temporary = signedUrls.TemporaryPath("downloads.show", TimeSpan.FromMinutes(15),
    values: new Dictionary<string, object?> { ["id"] = 42 });

var result = signedUrls.Validate(context.Request);
if (result.IsValid)
{
    var verified = result.Data!;
    // verified.Path, RouteName, RouteValues, Query, and ExpiresAt are authenticated.
}
```

`Path` and `Sign` also accept an explicit `expiresAt`. `Sign(existingPath)` works with the output of typed route URL
helpers. The `signature` query parameter contains base64url JSON followed by an HMAC-SHA256 signature. Its readable
payload includes the path, route name, supplied route values, decoded query values, and optional expiration.
Route defaults are represented in the path; `Sign` leaves the route name and supplied values empty.

Use `[RequireSignedUrl]` on a controller or action to return 403 for missing, invalid, or expired URLs. For manual
checks, use `context.HasValidSignedUrl()`, `context.ValidateSignedUrl()`, or `context.RequireSignedUrl()`, which returns
verified data or throws an HTTP 403 error. `Validate` reports missing signatures, malformed data, invalid signatures,
URL mismatches, and expiration; failed results never expose payload data.

Validation checks the escaped path and every query value against the signed payload. Query names ignore case,
independent query parameters can be reordered, and repeated values must keep their order. Duplicate signature
parameters are rejected, including encoded or differently cased aliases. Expiration uses the registered `TimeProvider`
and rejects a URL at the exact expiry instant. `SignatureParameterName` and `MaxUrlLength` can be configured at
registration; the defaults are `signature` and 32768 characters for the escaped path and query, including the token.
The scheme and authority of an absolute URL do not count toward the length limit.

Signing accepts application-relative URLs beginning with a single slash and no fragment. Validation also accepts
absolute HTTP(S) request URLs and ignores their origin. Signatures do not cover the host, scheme, HTTP method, or
request body. Tokens are readable and reusable until they expire, so keep sensitive data out of them and apply any
additional authentication, authorization, or single-use checks your endpoint needs.

## Socket.IO

`Serval.SocketIO` supports Socket.IO 4.x clients using the default JSON/binary parser: Socket.IO protocol 5 over
Engine.IO 4 (`EIO=4`). It accepts HTTP long-polling, direct WebSockets, and polling-to-WebSocket upgrades.
The interoperability suite uses the official JavaScript client, pinned to 4.8.4. Protocol details follow the
[Engine.IO](https://socket.io/docs/v4/engine-io-protocol/) and
[Socket.IO](https://socket.io/docs/v4/socket-io-protocol/) specifications.

Register fixed namespaces and derive handlers from `SocketIOHandler`:

```csharp
builder.Services.AddServal().AddSocketIO(socketIO =>
{
    socketIO.MapNamespace<ChatHandler>("/").MapNamespace<ChatHandler>("/chat");
}).AddHttpListener();

public sealed class ChatHandler : SocketIOHandler
{
    public override async ValueTask OnEventAsync(SocketIOEvent message, CancellationToken ct)
    {
        if (message.Name == "hello")
        {
            var name = message.Arguments.Get<string>(0);
            await message.Socket.EmitAsync("greeting", $"Hello, {name}!");
            if (message.ExpectsAcknowledgement)
            {
                await message.AcknowledgeAsync("received");
            }
        }
    }
}
```

The default endpoint is `/socket.io/`; the namespace `/chat` still uses that same HTTP endpoint. Configure the endpoint,
timeouts, JSON settings, origins, and limits through the second `AddSocketIO` callback. Route groups apply their prefix
and endpoint middleware. On HTTP transports without WebSocket upgrades, set `EnableWebSockets = false` and use polling.
Keep the framework's `MaxRequestBodyBytes` at least as large as the Socket.IO `MaxPayloadBytes`.

Each connecting, connected, event, and disconnected callback gets a fresh, asynchronously disposed DI scope.
Callbacks run in order per Engine.IO session, including across multiplexed namespaces. Acknowledgements are processed
separately, so a handler can await `socket.EmitWithAcknowledgementAsync("question", ["Ready?"], ct: ct)` without
blocking the reply. Callback deadlines are cooperative: handlers must observe `ct`. Exceptions close the session and
are logged; disconnect callbacks get a separate cleanup deadline. Connections can be retained for background sends,
but handlers and their scoped dependencies should stay within their callback.

Use `Arguments.Get<T>(index)` to deserialize an argument. `byte[]` values, including properties nested in objects and
arrays, travel as binary attachments. `EmitAsync` and `AcknowledgeAsync` accept multiple arguments. Acknowledgements
are explicit and can be sent once per event. Server acknowledgement waits have a timeout, support cancellation, and
end when the connection closes. Completion of a normal send means the message was queued, not that the peer received it.

Call `socket.Join("room")` or `socket.Leave("room")` for namespace-local rooms. Each accepted socket also joins its own
ID room. Inject `SocketIOServer` to broadcast from handlers or background services:

```csharp
await server.BroadcastAsync("/chat", "message", [new { text = "Hello, room!" }], room: "lobby");
```

An optional `except` socket ID excludes the sender. Rooms and pending acknowledgements are cleared on disconnect.
`socket.Disconnect()` ends that namespace; `socket.Disconnect(closeTransport: true)` ends all namespaces on the
connection and allows the official client's reconnection logic to run.

Authentication middleware can call `context.AuthenticateAsync()` before endpoint dispatch. The initial handshake's
principal is copied into each namespace socket. Override `OnConnectingAsync` to validate the client's `auth` object,
apply namespace authorization, and optionally assign a validated `socket.User`. Returning false sends `connect_error`
without disconnecting other namespaces. HTTP request services and credentials are not retained. Long-lived connections
keep their accepted principal; applications should disconnect or revalidate them when credentials expire or are revoked.
Browser origins are checked for both polling and WebSockets. Same-origin requests and requests without an Origin header
are accepted; add exact cross-origin URLs to `AllowedOrigins`. Origin checks do not replace authentication.

Queues, connections, rooms, namespaces, binary attachments, and pending acknowledgements have configurable limits.
Slow consumers are disconnected when their queues fill. Heartbeats detect dead peers; incomplete POST bodies, binary
packets, and upgrade probes have deadlines. Host shutdown cancels connections, awaits handlers, and removes rooms.
Delivery is ordered and at most once, with no replay after reconnecting. This first version uses in-memory state in a
single server process. Distributed adapters, connection-state recovery, WebTransport, legacy `EIO=3`, dynamic namespace
patterns, custom parsers, and serving the JavaScript client from the protocol endpoint are outside its scope.

## Samples

Run any sample with `dotnet run --project samples/<project-name>`.

| Sample                                                         | What it demonstrates                                                                   | Port |
|----------------------------------------------------------------|----------------------------------------------------------------------------------------|------|
| [Routing](samples/Serval.Sample.Routing)                       | Route constraints, typed bindings, URL helpers, and controller helpers.                | 8101 |
| [TodoApi](samples/Serval.Sample.TodoApi)                       | JSON APIs, body binding, validation, and generated JSON metadata.                      | 8102 |
| [Middleware](samples/Serval.Sample.Middleware)                 | Wrapping application and route execution, ordering, and short-circuiting.              | 8103 |
| [Files](samples/Serval.Sample.Files)                           | Static files, downloads, and buffered raw uploads.                                     | 8104 |
| [Streaming](samples/Serval.Sample.Streaming)                   | Interactive SSE reconnection and WebSocket echo clients, plus streamed JSON.           | 8105 |
| [SocketIO](samples/Serval.Sample.SocketIO)                     | Room chat, namespace selection, binary echo, acknowledgements, and transport upgrades. | 8119 |
| [CookieAuth](samples/Serval.Sample.CookieAuth)                 | Session cookies, sign-in, sign-out, and CSRF checks.                                   | 8106 |
| [BearerAuth](samples/Serval.Sample.BearerAuth)                 | Bearer authentication, policies, and resource authorization.                           | 8107 |
| [OAuth](samples/Serval.Sample.OAuth)                           | Provider configuration and sign-in backed by cookie sessions.                          | 8108 |
| [Exceptions](samples/Serval.Sample.Exceptions)                 | Custom error handling across actions, middleware, and responses.                       | 8109 |
| [Transports](samples/Serval.Sample.Transports)                 | HTTP transport selection and protocol examples.                                        | 8110 |
| [EntityFramework](samples/Serval.Sample.EntityFramework)       | Request-scoped database access with EF Core and SQLite.                                | 8111 |
| [BackgroundServices](samples/Serval.Sample.BackgroundServices) | Startup work, background imports, and shared state.                                    | 8112 |
| [Savanna](samples/Serval.Sample.Savanna)                       | Typed templates, nested layouts, includes, and components.                             | 8113 |
| [Vite](samples/Serval.Sample.Vite)                             | Generated asset references, Savanna rendering, and frontend HMR.                       | 8114 |
| [SignedUrls](samples/Serval.Sample.SignedUrls)                 | Permanent and expiring links, signed payloads, middleware, and manual validation.      | 8115 |
| [Forms](samples/Serval.Sample.Forms)                           | Browser forms, JSON POST values, multipart files, and route/query helpers.             | 8116 |
| [Lifecycles](samples/Serval.Sample.Lifecycles)                 | Request initialization, final headers, completion outcomes, and local callbacks.       | 8117 |

Run `dotnet run --project samples/Serval.Sample.Streaming` and open <http://localhost:8105/> to try SSE and WebSockets.
The page uses native `EventSource` and `WebSocket` clients with no frontend dependencies. Start the event feed to
watch it reconnect every three events using `Last-Event-ID`, or connect the echo client and send text or binary
messages. The server code is in `StreamsController.cs`; the client code is in `Public/streaming/`.
Use `curl.exe -N http://localhost:8105/events` for the full ten-event stream, or add `-H "Last-Event-ID: 7"` to resume
at event 8. `/events?batch=3` limits each response to three events; a reconnect after event 10 returns HTTP 204.
`/records` streams five newline-delimited JSON records. Use `curl` on macOS or Linux.

The Socket.IO sample needs `npm ci --prefix samples/Serval.Sample.SocketIO --ignore-scripts` first, then
`dotnet run --project samples/Serval.Sample.SocketIO`. Open <http://localhost:8119/> in two tabs and join the same
room. The page offers polling, WebSockets, and upgrades, and uses the official client copied into the published
assets. No CDN is needed at runtime.

The Vite sample needs Node.js 24 and `npm ci --prefix samples/Serval.Sample.Vite/ClientApp --ignore-scripts` first.
For HMR, run it with `-p:ViteDevelopment=true`, start `npm --prefix samples/Serval.Sample.Vite/ClientApp run dev` in a
second terminal, and open <http://localhost:5173/>. Its project file and Vite configuration show how the two servers fit
together. The transports sample accepts `-- --http2` or `-- --http3`; HTTP/3 requires QUIC support on the host.

## Working on the repository

- [src/](src): framework packages, generators, the shared Savanna compiler, and the Savanna language server.
- [tests/](tests): framework and language-server tests, grouped by subsystem.
- [samples/](samples): runnable applications with their own assets and configuration.
- [integrations/Rider/](integrations/Rider): the Savanna plugin, which bundles the language server.

Build and test the solution from the repository root:

```powershell
npm ci --prefix samples/Serval.Sample.Vite/ClientApp --ignore-scripts
npm ci --prefix samples/Serval.Sample.SocketIO --ignore-scripts
npm ci --prefix tests/Serval.SocketIO.Tests --ignore-scripts
dotnet restore Serval.slnx
dotnet build Serval.slnx --configuration Release --no-restore
dotnet test Serval.slnx --configuration Release --no-build --no-restore
```

NuGet packages land in each package project's `bin/Release` directory. Use `-p:GeneratePackageOnBuild=false` when you
only need assemblies. Additional [package](tests/Serval.Tests/Packaging/VerifyVitePackage.ps1) and
[HMR](tests/Serval.Tests/Packaging/VerifyViteDevelopment.mjs) checks cover Vite integration.

To run only Socket.IO checks, use `dotnet test tests/Serval.SocketIO.Tests --configuration Release` after installing
the two Socket.IO npm dependencies above. The test project starts a real local server and invokes Node.js 24 with
the pinned official client. Missing Node.js or client dependencies fail the tests rather than skipping interoperability.

The [Socket.IO benchmarks](benchmarks/Serval.SocketIO.Benchmarks) measure packet processing time and allocations,
plus acknowledgement throughput and latency over polling and WebSockets. They use the test project's pinned client.
Build once in Release, then run the measurements without a build running alongside them:

```powershell
dotnet build benchmarks/Serval.SocketIO.Benchmarks -c Release -p:GeneratePackageOnBuild=false
dotnet benchmarks/Serval.SocketIO.Benchmarks/bin/Release/net11.0/Serval.SocketIO.Benchmarks.dll
dotnet benchmarks/Serval.SocketIO.Benchmarks/bin/Release/net11.0/Serval.SocketIO.Benchmarks.dll server 8120
# In a second terminal, while the benchmark server is running:
node benchmarks/Serval.SocketIO.Benchmarks/load.mjs http://localhost:8120
```

The load test checks every reply and reports JSON with five samples per case, four clients, and eight requests in
flight per client. Compare runs on the same machine; loopback results do not represent production capacity.

The Rider plugin provides Savanna highlighting, completion, navigation, and diagnostics. It targets Rider 2026.3 and
requires the .NET SDK; building it also needs JDK 25. On Windows:

```powershell
.\integrations\Rider\gradlew.bat -p integrations/Rider test buildPlugin --no-daemon --console=plain
```

Install the ZIP from `integrations/Rider/build/distributions` through Rider's plugin settings.
