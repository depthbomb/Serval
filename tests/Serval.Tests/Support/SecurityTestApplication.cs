#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Security.Claims;
using Serval.Authentication;
using Serval.Hosting;
using Serval.Http;
using Serval.Http.Constants;
using Serval.Routing;

namespace Serval.Tests.Support;

internal sealed class SecurityTestApplication : IAsyncDisposable
{
    public ServiceProvider  Services { get; }
    public TestTimeProvider Clock    { get; } = new();

    public SecurityTestApplication(Action<ServalBuilder> configure)
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.ClearProviders());
        services.AddSingleton<TimeProvider>(Clock);
        var builder = services.AddServal();
        configure(builder);
        Services = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    public ValueTask DisposeAsync() => Services.DisposeAsync();

    public SecurityTestRequest Request(string url = "https://app.test/", string? cookie = null, string method = "GET", Action<Headers>? headers = null, CancellationToken cancellationToken = default)
        => new(Services, url, cookie, method, headers, cancellationToken);

    public static ClaimsPrincipal User(string name = "alice", params string[] roles)
        => new(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, "user-1"), new Claim(ClaimTypes.Name, name) }
            .Concat(roles.Select(role => new Claim(ClaimTypes.Role, role))), "test"));

    public static AuthenticationResult Success(string scheme = "test")
        => AuthenticationResult.Success(new AuthenticationTicket(User(), scheme, new AuthenticationProperties()));

    public static string Cookie(MemoryResponse response, string? prefix = null)
        => response.Headers.GetValues(HeaderNames.SetCookie).Single(value => prefix is null || value.StartsWith(prefix, StringComparison.Ordinal)).Split(';')[0];
}

internal sealed class SecurityTestRequest : IAsyncDisposable
{
    public           RequestContext    Context  { get; }
    public           MemoryResponse    Response { get; } = new();
    private readonly AsyncServiceScope _scope;

    public SecurityTestRequest(ServiceProvider services, string url, string? cookie, string method, Action<Headers>? configureHeaders, CancellationToken cancellationToken)
    {
        _scope = services.CreateAsyncScope();
        var headers = new Headers { [HeaderNames.Cookie] = cookie };
        configureHeaders?.Invoke(headers);
        var request = new Request(method, new Uri(url), HttpVersion.Version11, headers, Stream.Null, 1024);
        Context = new RequestContext(request, Response, _scope.ServiceProvider, services.GetRequiredService<IUrlGenerator>(), cancellationToken);
    }

    public ValueTask DisposeAsync() => _scope.DisposeAsync();
}

internal sealed class TestTimeProvider : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Now;
}
