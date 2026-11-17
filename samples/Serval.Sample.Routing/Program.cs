#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serval.Generated;
using Serval.Hosting;
using Serval.Transport.HttpListener;

namespace Serval.Sample.Routing;

public static class Program
{
    public static async Task Main(string[] args)
    {
        var host = Build(args);
        await host.RunAsync();
    }

    public static IHost Build(string[] args)
    {
        var host = Host.CreateApplicationBuilder(args);
        var port = host.Configuration.GetValue("port", 8101);

        host.Services.AddSingleton(TimeProvider.System);
        host.Services.AddServal(options =>
        {
            options.TrailingSlashes          = TrailingSlashBehavior.RedirectToWithoutSlash;
            options.RouteConstraints["slug"] = (value, _) => value.Length is > 0 and <= 64 && value.All(character => char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character == '-');
        }).AddGeneratedControllers().AddEndpoint("GET", "/health", context => context.Response.WriteTextAsync("ready"), name: "health").AddHttpListener(options =>
        {
            options.Prefixes.Clear();
            options.Prefixes.Add($"http://localhost:{port}/");
        });

        return host.Build();
    }
}
