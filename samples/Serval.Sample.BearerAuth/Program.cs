#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serval.Authentication;
using Serval.Authentication.Authorization;
using Serval.Generated;
using Serval.Hosting;
using Serval.Transport.HttpListener;

namespace Serval.Sample.BearerAuth;

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
        var port = host.Configuration.GetValue("port", 8107);
        host.Services.AddSingleton<DocumentStore>();
        host.Services.AddServal().AddGeneratedControllers().AddHttpListener(options =>
        {
            options.Prefixes.Clear();
            options.Prefixes.Add($"http://localhost:{port}/");
        }).AddAuthentication(options =>
        {
            options.DefaultScheme             = "Bearer";
            options.Policies["reports.read"]  = new AuthorizationPolicy().RequireClaim("permission", "reports.read");
            options.Policies["reports.write"] = new AuthorizationPolicy().RequireRole("editor").RequireClaim("permission", "reports.write");
            options.Policies["reports.owner"] = new AuthorizationPolicy().RequireClaim("permission", "reports.read").Require((context, user) =>
                ValueTask.FromResult(string.Equals(context.RouteValues["owner"], user.Identity?.Name, StringComparison.Ordinal)));
        }).AddVoter<DocumentAccessVoter>().AddVoter<LockedDocumentVoter>().AddBearer("Bearer", options =>
        {
            // Public opaque demo tokens over loopback HTTP. Keep RequireHttps enabled
            // and supply a real token validator for a deployed API.
            options.RequireHttps  = false;
            options.ValidateToken = (_, token) => ValueTask.FromResult(DemoTokens.Validate(token));
        });

        return host.Build();
    }
}
