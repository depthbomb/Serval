#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Text;
using Serval.Generated;
using Serval.Hosting;
using Serval.Http;
using Serval.Http.Constants;

namespace Serval.Tests.Support;

internal sealed class TestApplication : IAsyncDisposable
{
    public ServiceProvider Services { get; }

    public TestApplication(Action<ServalOptions>? configure = null, Action<IServiceCollection>? configureServices = null, Action<ServalBuilder>? configureBuilder = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.ClearProviders());
        services.AddScoped<ScopedProbe>();
        var builder = services.AddServal(configure).AddGeneratedControllers();
        configureBuilder?.Invoke(builder);
        configureServices?.Invoke(services);
        Services = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    public ValueTask DisposeAsync() => Services.DisposeAsync();

    public async Task<MemoryResponse> SendAsync(string path, string method = HttpMethods.Get, string? body = null, string? contentType = null, Action<Headers>? configureHeaders = null, CancellationToken cancellationToken = default)
    {
        var headers = new Headers { [HeaderNames.ContentType] = contentType };
        configureHeaders?.Invoke(headers);
        var response = new MemoryResponse();
        var request  = new Request(method, new Uri("http://localhost" + path), HttpVersion.Version11, headers, new MemoryStream(Encoding.UTF8.GetBytes(body ?? "")), Services.GetRequiredService<ServalOptions>().MaxRequestBodyBytes);
        await Services.GetRequiredService<IRequestHandler>().HandleAsync(request, response, cancellationToken);
        return response;
    }
}
