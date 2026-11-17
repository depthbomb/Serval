#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Net;
using System.Text;
using Serval.Hosting;
using Serval.Http;
using Serval.Http.Constants;
using Serval.Sample.Routing;

namespace Serval.Tests.Support;

internal sealed class SampleApplication : IAsyncDisposable
{
    public IHost Host { get; }

    public SampleApplication(string name, params string[] arguments)
    {
        Host = Build(name, arguments);
    }

    public ValueTask DisposeAsync() => ((IAsyncDisposable)Host).DisposeAsync();

    public async Task<MemoryResponse> SendAsync(string path, string method = "GET", string? body = null, Action<Headers>? configure = null)
    {
        var headers = new Headers();
        if (body is not null)
        {
            headers[HeaderNames.ContentType] = MimeTypes.Json;
        }

        configure?.Invoke(headers);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(body ?? ""));
        var request = new Request(method, new Uri("http://localhost" + path), HttpVersion.Version11, headers, stream,
            Host.Services.GetRequiredService<ServalOptions>().MaxRequestBodyBytes);
        var response = new MemoryResponse();
        await Host.Services.GetRequiredService<IRequestHandler>().HandleAsync(request, response, CancellationToken.None);
        return response;
    }

    public static IHost Build(string name, string[] arguments)
    {
        string[] args = ["--Logging:LogLevel:Default", "None", .. arguments];
        return name switch
        {
            "Routing"    => Program.Build(args),
            "Savanna"    => Sample.Savanna.Program.Build(args),
            "Vite"       => Sample.Vite.Program.Build(args),
            "TodoApi"    => Sample.TodoApi.Program.Build(args),
            "Middleware" => Sample.Middleware.Program.Build(args),
            "Lifecycles" => Sample.Lifecycles.Program.Build(args),
            "Files"      => Sample.Files.Program.Build(args),
            "Forms"      => Sample.Forms.Program.Build(args),
            "Streaming"  => Sample.Streaming.Program.Build(args),
            "CookieAuth" => Sample.CookieAuth.Program.Build(args),
            "BearerAuth" => Sample.BearerAuth.Program.Build(args),
            "OAuth"      => Sample.OAuth.Program.Build([.. args, "--OAuth:ClientId", ""]),
            "SignedUrls" => Sample.SignedUrls.Program.Build(args),
            "Exceptions" => Sample.Exceptions.Program.Build(args),
            _            => throw new ArgumentOutOfRangeException(nameof(name)),
        };
    }
}
