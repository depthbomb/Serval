# Serval

Serval is an async, controller-based web framework for .NET. It uses `Microsoft.Extensions.Hosting`, so your
endpoints share configuration, dependency injection, logging, and background services with the rest of your app.

Start with the core package and a transport, then add authentication, background jobs, templates, or frontend assets
as you need them. Serval is a pre-1.0 project; APIs and template syntax may change.

## Try it out

Use the .NET SDK in [global.json](global.json). The project targets .NET 11 and C# 15.

```powershell
dotnet run --project samples/Serval.Sample.Routing
```

Open <http://localhost:8101/> to try the routes. Add `-- --port 8201` to use a different port.

For your own app, reference `Serval` and `Serval.Transport.HttpListener`. In a .NET 11 console project with implicit
usings enabled:

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
`Task<IActionResult>` or `ValueTask<IActionResult>`. Inheriting `ControllerBase` is optional; it provides helpers
for reading input and returning responses.

For HTTP/2 and optional HTTP/3, use [Kestrel hosting](#hosting-with-kestrel). HttpListener remains the simpler
alternative.

NuGet packages include their source generators. When using project references, add the analyzers explicitly as shown
in the [routing sample project](samples/Serval.Sample.Routing/Serval.Sample.Routing.csproj).
`AddGeneratedControllers()` registers the current assembly's controllers. For an assembly named `MyLibrary`, use
`Serval.Generated.MyLibraryControllers.Register(serval)` with your `ServalBuilder`.

## Packages

Build packages locally or use your internal feed.

| Package                                                            | Purpose                                                                       |
|--------------------------------------------------------------------|-------------------------------------------------------------------------------|
| [Serval](src/Serval)                                               | Controllers, routing, middleware, static files, and streaming.                |
| [Serval.Authentication](src/Serval.Authentication)                 | Cookie and bearer authentication, authorization, password hashing, and CSRF.  |
| [Serval.OAuth](src/Serval.OAuth)                                   | OAuth 2.0 clients and OpenID Connect sign-in.                                 |
| [Serval.SignedUrls](src/Serval.SignedUrls)                         | Signed links with optional expiration.                                        |
| [Serval.Jobs](src/Serval.Jobs)                                     | Recurring jobs with periodic or cron schedules.                               |
| [Serval.SocketIO](src/Serval.SocketIO)                             | Socket.IO events, acknowledgements, namespaces, and rooms.                    |
| [Serval.Savanna](src/Serval.Savanna)                               | Compiled HTML templates with typed models, layouts, and components.           |
| [Serval.Vite](src/Serval.Vite)                                     | Generated asset references and Vite development server support.               |
| [Serval.Transport.HttpListener](src/Serval.Transport.HttpListener) | HTTP and WebSockets; used by most samples.                                    |
| [Serval.Transport.Kestrel](src/Serval.Transport.Kestrel)           | Kestrel HTTP/1.1, HTTP/2, HTTP/3, and WebSockets through the Serval pipeline. |

## Hosting with Kestrel

Reference `Serval` and `Serval.Transport.Kestrel`, then use an ASP.NET Core host:

```csharp
using Microsoft.AspNetCore.Builder;
using Serval.Generated;
using Serval.Hosting;
using Serval.Transport.Kestrel;

public static class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateSlimBuilder(args);
        builder.Services.AddServal().AddGeneratedControllers();

        await using var application = builder.Build();
        application.UseServal();
        await application.RunAsync();
    }
}
```

Configure addresses, certificates, protocols, and server limits through `builder.WebHost.ConfigureKestrel`.
When using `CreateSlimBuilder`, also call `builder.WebHost.UseQuic()` to enable HTTP/3; these extensions use
the `Microsoft.AspNetCore.Hosting` namespace.
`UseServal()` enables WebSockets and sends all requests reaching it through Serval's routing, middleware, and request
scopes. ASP.NET middleware may run before it. HTTP/2 WebSocket requests use CONNECT, so register a CONNECT endpoint
when accepting those connections. Serval does not expose an Early Hints sending API.

Both Kestrel's limits and Serval's limits apply; increase both body limits when accepting large uploads. Body parser
rejections retain their HTTP error status. Request cancellation includes client disconnects and host shutdown.
The Kestrel package requires the `Microsoft.AspNetCore.App` shared framework, supplied by the ASP.NET Core runtime
or included in a self-contained deployment. The core Serval package has no ASP.NET Core framework reference.

Run `dotnet run --project samples/Serval.Sample.Transports` for Kestrel HTTP on port 8110 and HTTPS with
HTTP/1.1 and HTTP/2 on port 8443. Add `-- --http3` to also enable HTTP/3, or `-- --http-listener` to use
HttpListener with HTTP/1.1 only. The generated HTTPS certificate is temporary and untrusted. Use `--port` and
`--https-port` to change those ports. HTTP/3 requires platform QUIC support.

## Lifecycle handlers and middleware

Use middleware to wrap request processing or return a response early. Application middleware runs in registration
order and calls `await next(context)` to continue. `UseMiddleware<T>()` supports constructor injection; endpoint
middleware attributes apply to matched controller actions.

Use a `RequestLifecycleHandler` for work at a specific point in the request:

| Hook                      | Use it to                                               |
|---------------------------|---------------------------------------------------------|
| `OnRequestReceivedAsync`  | Initialize scoped state before middleware and routing.  |
| `OnResponseStartingAsync` | Set final headers before the response starts.           |
| `OnRequestCompletedAsync` | Record the final status, duration, exception, or abort. |

Register handlers with `serval.AddRequestLifecycleHandler<T>(priority: 0)`. Each handler is scoped to the request,
and lower priorities run first. Local `Response.OnStarting` and `Response.OnCompleted` callbacks run before the
corresponding lifecycle hooks.

Keep response writes out of lifecycle hooks. Completion runs before resource and request-scope disposal, with a
separate cancellation budget set by `ServalOptions.CompletionTimeout` (five seconds by default). Completion handlers
must observe that token. Send longer work to a background service without retaining request-scoped dependencies.

The [middleware sample](samples/Serval.Sample.Middleware) shows timing and maintenance responses. The
[lifecycles sample](samples/Serval.Sample.Lifecycles) shows request state, final headers, and completion logging.

## Endpoint policies and groups

Use groups to share a route prefix and endpoint settings:

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

Groups can nest and accept a `namePrefix` for named routes. Parent conventions run before child conventions.
Endpoint body limits must fit within the application's `MaxRequestBodyBytes` ceiling. Configure form limits through
`endpoint.Forms`, and read bodies after endpoint selection so those limits apply.

`context.Endpoint` exposes the selected route and its metadata. For custom dispatch, `AddEndpoint` registers a
delegate directly, while `DispatchWith` replaces the default routing and static-file sequence. The public
`IEndpointMatcher`, `RoutingMiddleware`, and `StaticFileMiddleware` APIs let you compose those steps yourself.

## Request values and forms

Controllers can use binding attributes or read values directly:

```csharp
// Inside a ControllerBase action:
var id = RouteValue<int>("id");
var page = QueryValue("page", 1);

var post = await ReadPostAsync();
var username = post.Get<string>("username");
var enabled = post.Get("enabled", false);
var ids = post.GetValues<int>("ids");
```

`ReadPostAsync()` reads URL-encoded forms, multipart forms, and JSON objects, including on PUT and PATCH requests.
Required reads reject missing or invalid values with 400. Fallbacks apply only to absent names; `TryGet<T>()` handles
optional reads. Repeated fields and JSON arrays are available through `GetValues<T>()`.

Use `[FromForm]` or `ReadFormAsync()` for form fields and uploaded files. Copy uploads to an application-owned
destination before the request ends, and treat filenames and content types as client input. Form and `[FromBody]`
parameters cannot share an action.

Choose a body-reading mode before reading: cached form/POST data cannot be mixed with raw readers.
`CopyBodyToAsync` streams a raw body, while `ReadMultipartAsync` processes multipart sections as they arrive.
Finish each section inside its callback, and only publish uploaded files once the whole operation succeeds.

The total body limit is 1 MiB by default. Configure `ServalOptions.Forms` for field and file limits, buffering, and
temporary storage; those settings still respect the total body limit. See the
[forms sample](samples/Serval.Sample.Forms) for browser forms, JSON values, and buffered or streaming uploads.

## Input validation

Register a scoped `IInputValidator<T>` with `serval.AddInputValidator<SignupInput, SignupValidator>()`.
Validators add field errors through `errors.Add("email", "Enter a valid email address.")`.

Use `[FromBody(Validate = true)]` to validate a bound model before the action runs. Without that opt-in, body binding
only deserializes. For models assembled in an action, call `await ValidateAsync(input)`.

Invalid input returns 422 with `code: "validation_failed"` and an `errors` object. To redisplay an HTML form or choose
your own response, use `InputValidation.EvaluateAsync(context, input)` and inspect `IsValid` and `Errors`.
See the [Todo API sample](samples/Serval.Sample.TodoApi) for a working example.

## Proxies and diagnostics

Configure trusted proxies before middleware that uses client addresses or public URLs:

```csharp
serval.UseForwardedHeaders(options =>
{
    options.KnownProxies.Add(IPAddress.Parse("10.0.0.10"));
    options.AllowedHosts.Add("app.example.com");
    options.ForwardLimit = 1;
});
```

No proxies are trusted by default. The middleware reads `X-Forwarded-For` and matching forwarded protocol, host,
and prefix headers. `ClientAddress`, `Scheme`, `Host`, `PathBase`, and `PublicUrl` then describe the public request;
`Connection` and `Url` retain the transport values. Route helpers return application-relative paths, so prepend
`Request.PathBase` when publishing links under an external mount.

For tracing and metrics, subscribe to the `Serval` ActivitySource and Meter. They report active requests, duration,
errors, and matched route templates without recording query strings, bodies, or credentials.

## Signed URLs

`Serval.SignedUrls` creates permanent or expiring links and validates their route and query values.

```csharp
serval.AddSignedUrls(options =>
{
    options.SigningKey = Convert.FromBase64String(builder.Configuration["SignedUrls:Key"]
        ?? throw new InvalidOperationException("Configure SignedUrls:Key."));
});
```

Use a cryptographically random key of at least 32 bytes, stored in secret configuration. Keep it consistent across
instances and restarts; changing it invalidates existing links.

Inject `ISignedUrlService` to create links:

```csharp
var link = signedUrls.TemporaryPath("downloads.show", TimeSpan.FromMinutes(15),
    values: new Dictionary<string, object?> { ["id"] = 42 });
```

Use `Path` for a permanent link or `Sign` for an existing application-relative path. Apply `[RequireSignedUrl]` to
return 403 for missing, invalid, or expired signatures. Manual checks use `context.ValidateSignedUrl()` or
`context.RequireSignedUrl()`.

Signed data is readable, and links remain reusable until they expire. Signatures cover the path and query, but
exclude the host, HTTP method, and body. Keep sensitive data out of links and apply any other authorization your
endpoint needs. The [signed URL sample](samples/Serval.Sample.SignedUrls) demonstrates generation and validation.

## Recurring jobs

Implement `IJob` and add a schedule attribute:

```csharp
using Microsoft.Extensions.Logging;
using Serval.Jobs;

[PeriodicJob(30_000, Name = "cleanup")]
public sealed class CleanupJob : IJob
{
    private readonly ILogger<CleanupJob> _logger;

    public CleanupJob(ILogger<CleanupJob> logger)
    {
        _logger = logger;
    }

    public ValueTask ExecuteAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _logger.LogInformation("Cleanup ran.");
        return ValueTask.CompletedTask;
    }
}
```

Import `Serval.Generated` and call `serval.AddGeneratedJobs()` once during host setup. Use
`[CronJob("0 2 * * *", Name = "cleanup")]` for a daily 02:00 UTC schedule, or set `TimeZoneId` for another time zone.
Attributes can also reference static schedule members; the [jobs sample](samples/Serval.Sample.Jobs) shows this
with Caprine's duration and cron generators.

Inject `JobRegistry` to inspect jobs, pause or resume schedules, and trigger a run with `RunAsync(name, ct)`.
Manual runs are allowed while paused and return false if that job is already running.

Each execution gets a fresh DI scope. A job never overlaps itself, and scheduled failures are logged before the next
tick. Jobs must observe cancellation so shutdown can finish. Schedules and state live in one host's memory; there is
no persistence, distributed locking, or replay after restarts.

## Socket.IO

`Serval.SocketIO` supports Socket.IO 4.x clients using the default JSON/binary parser, with HTTP polling,
WebSockets, and polling-to-WebSocket upgrades.

```csharp
builder.Services.AddServal().AddSocketIO(socketIO =>
{
    socketIO.MapNamespace<ChatHandler>("/chat");
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

The default endpoint is `/socket.io/`, shared by all namespaces. Use `socket.Join` and `socket.Leave` for rooms,
or inject `SocketIOServer` to broadcast. Events support multiple arguments and binary attachments.

Each handler callback gets its own DI scope and must observe its cancellation token. Use `OnConnectingAsync` to
validate client authentication and authorize a namespace. Add exact cross-origin URLs to `AllowedOrigins` when
needed; origin checks do not replace authentication.

Set `EnableWebSockets = false` on transports without upgrade support, and keep Serval's `MaxRequestBodyBytes` at
least as large as Socket.IO's `MaxPayloadBytes`. Queues and connections have configurable limits; slow consumers
are disconnected. Delivery is ordered and at most once, with in-memory rooms and no replay or recovery after
reconnecting.

The [Socket.IO sample](samples/Serval.Sample.SocketIO) includes room chat, binary echo, and acknowledgements.

## Samples

Run a sample with `dotnet run --project samples/<project-name>`.

| Sample                                                         | Covers                                           | Port |
|----------------------------------------------------------------|--------------------------------------------------|------|
| [Routing](samples/Serval.Sample.Routing)                       | Routes, binding, and URL helpers.                | 8101 |
| [TodoApi](samples/Serval.Sample.TodoApi)                       | JSON APIs and input validation.                  | 8102 |
| [Middleware](samples/Serval.Sample.Middleware)                 | Ordering, timing, and early responses.           | 8103 |
| [Files](samples/Serval.Sample.Files)                           | Static files, downloads, and uploads.            | 8104 |
| [Streaming](samples/Serval.Sample.Streaming)                   | SSE, WebSockets, and streamed JSON.              | 8105 |
| [CookieAuth](samples/Serval.Sample.CookieAuth)                 | Sessions, sign-in, sign-out, and CSRF.           | 8106 |
| [BearerAuth](samples/Serval.Sample.BearerAuth)                 | Bearer tokens and authorization policies.        | 8107 |
| [OAuth](samples/Serval.Sample.OAuth)                           | OAuth and OIDC sign-in with local sessions.      | 8108 |
| [Exceptions](samples/Serval.Sample.Exceptions)                 | Custom error handling.                           | 8109 |
| [Transports](samples/Serval.Sample.Transports)                 | Kestrel with optional HTTP/3, or HttpListener.   | 8110 |
| [EntityFramework](samples/Serval.Sample.EntityFramework)       | EF Core and request-scoped database access.      | 8111 |
| [BackgroundServices](samples/Serval.Sample.BackgroundServices) | Startup work and background services.            | 8112 |
| [Savanna](samples/Serval.Sample.Savanna)                       | Typed templates, layouts, and components.        | 8113 |
| [Vite](samples/Serval.Sample.Vite)                             | Asset references and frontend HMR.               | 8114 |
| [SignedUrls](samples/Serval.Sample.SignedUrls)                 | Signed links and expiration.                     | 8115 |
| [Forms](samples/Serval.Sample.Forms)                           | Form fields, JSON values, and file uploads.      | 8116 |
| [Lifecycles](samples/Serval.Sample.Lifecycles)                 | Request hooks and response callbacks.            | 8117 |
| [SocketIO](samples/Serval.Sample.SocketIO)                     | Chat rooms, binary events, and acknowledgements. | 8119 |
| [Jobs](samples/Serval.Sample.Jobs)                             | Periodic and cron jobs with runtime controls.    | 8121 |

The Socket.IO and Vite samples need Node.js 24. Install their dependencies before running:

```powershell
npm ci --prefix samples/Serval.Sample.SocketIO --ignore-scripts
npm ci --prefix samples/Serval.Sample.Vite/ClientApp --ignore-scripts
```

For Vite HMR, run the sample with `-p:ViteDevelopment=true`, start
`npm --prefix samples/Serval.Sample.Vite/ClientApp run dev` in another terminal, and open <http://localhost:5173/>.
The transports sample uses Kestrel by default. Add `-- --http3` for HTTP/3 or `-- --http-listener` for HttpListener.
The OAuth sample needs provider configuration; see the [OAuth package README](src/Serval.OAuth/README.md).

## Working on the repository

The [src](src), [tests](tests), and [samples](samples) directories contain the packages, tests, and example apps.
The Savanna Rider plugin lives in [integrations/Rider](integrations/Rider).

Build and test from the repository root, with Node.js 24 installed:

```powershell
npm ci --prefix samples/Serval.Sample.Vite/ClientApp --ignore-scripts
npm ci --prefix samples/Serval.Sample.SocketIO --ignore-scripts
npm ci --prefix tests/Serval.SocketIO.Tests --ignore-scripts
dotnet restore Serval.slnx
dotnet build Serval.slnx --configuration Release --no-restore
dotnet test Serval.slnx --configuration Release --no-build --no-restore
```

Packages are written to each project's `bin/Release` directory. Use `-p:GeneratePackageOnBuild=false` when you only
need assemblies. Additional checks cover [Vite packaging](tests/Serval.Tests/Packaging/VerifyVitePackage.ps1),
[Vite HMR](tests/Serval.Tests/Packaging/VerifyViteDevelopment.mjs), and
[jobs packaging](tests/Serval.Jobs.Tests/VerifyJobsPackage.ps1).

<details>
<summary>Running benchmarks</summary>

Build in Release and run without concurrent builds or tests. Compare results on the same machine.

```powershell
dotnet build benchmarks/Serval.Jobs.Benchmarks -c Release -p:GeneratePackageOnBuild=false
$env:DOTNET_TieredCompilation = '0'
dotnet benchmarks/Serval.Jobs.Benchmarks/bin/Release/net11.0/Serval.Jobs.Benchmarks.dll
Remove-Item Env:DOTNET_TieredCompilation
```

The Socket.IO load test uses the test project's npm dependencies installed above:

```powershell
dotnet build benchmarks/Serval.SocketIO.Benchmarks -c Release -p:GeneratePackageOnBuild=false
dotnet benchmarks/Serval.SocketIO.Benchmarks/bin/Release/net11.0/Serval.SocketIO.Benchmarks.dll
dotnet benchmarks/Serval.SocketIO.Benchmarks/bin/Release/net11.0/Serval.SocketIO.Benchmarks.dll server 8120
# In a second terminal:
node benchmarks/Serval.SocketIO.Benchmarks/load.mjs http://localhost:8120
```

Compare server transports with a separate server process for each trial. The harness verifies response bytes and
the exact HTTP version, rotates transport order, and reports throughput, latency, server allocations, and server CPU.
Results are loopback measurements with a client on the same machine. Run without concurrent builds or tests:

```powershell
dotnet build benchmarks/Serval.Transport.Benchmarks -c Release -p:GeneratePackageOnBuild=false
$env:DOTNET_TieredCompilation = '0'
dotnet benchmarks/Serval.Transport.Benchmarks/bin/Release/net11.0/Serval.Transport.Benchmarks.dll --transports listener,kestrel1,kestrel2,kestrel3 --output "$env:TEMP/serval-transports.json"
Remove-Item Env:DOTNET_TieredCompilation
```

The defaults are five repetitions, two seconds of warmup, and three seconds of measurement per workload and transport.
Override these with `--repetitions`, `--warmup`, and `--seconds`. HTTP/2 uses TLS, HTTP/3 uses QUIC, and HTTP/1.1 is
cleartext for both HttpListener and Kestrel. Compare implementations of the same protocol when assessing performance.

Use `--workloads` to select comma-separated workloads: `small-sequential`, `small-concurrent`, `headers-concurrent`,
`echo-64KiB`, and `download-256KiB`. The header workload adds twelve request headers to the small concurrent response.
For a before-and-after comparison, copy the complete benchmark output directory before changing the implementation,
then pass `--baseline <saved-directory>/Serval.Transport.Benchmarks.dll` to the rebuilt harness. It interleaves the saved
and current server binaries, using the same client workloads, and labels each result with its variant.

</details>

<details>
<summary>Building the Rider plugin</summary>

The plugin provides Savanna highlighting, completion, navigation, and diagnostics for Rider 2026.3.
Building it requires the .NET SDK and JDK 25. On Windows:

```powershell
.\integrations\Rider\gradlew.bat -p integrations/Rider test buildPlugin --no-daemon --console=plain
```

Install the ZIP from `integrations/Rider/build/distributions` through Rider's plugin settings.

</details>
