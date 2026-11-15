#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serval.Authentication;
using Serval.Authentication.Passwords;
using Serval.Generated;
using Serval.Hosting;
using Serval.Http;
using Serval.Transport.HttpListener;

namespace Serval.Sample.CookieAuth;

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
        var port = host.Configuration.GetValue("port", 8106);
        host.Services.AddSingleton<IPasswordAccountStore, DemoAccountStore>();
        host.Services.AddServal(options => options.MaxRequestBodyBytes = 4096).AddGeneratedControllers().AddHttpListener(options =>
        {
            options.Prefixes.Clear();
            options.Prefixes.Add($"http://localhost:{port}/");
        }).AddAuthentication(options => options.DefaultScheme = "Cookies").AddCookie(configure: options =>
        {
            // Only the loopback demo uses HTTP. Use Secure and a __Host- name over HTTPS.
            options.CookieName = "Serval.Sample.CookieAuth";
            options.Cookie     = new CookieOptions { Secure = false, SameSite = SameSite.Strict };
            options.Lifetime   = TimeSpan.FromMinutes(20);
        }).AddPasswordAccounts<DemoAccountStore>();

        return host.Build();
    }
}
