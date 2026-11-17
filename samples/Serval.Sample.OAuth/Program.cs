#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using System.Security.Claims;
using System.Text.Json;
using Serval.Hosting;
using Serval.Http;
using Serval.OAuth;
using Serval.Transport.HttpListener;

namespace Serval.Sample.OAuth;

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
        var port = host.Configuration.GetValue("port", 8108);
        var serval = host.Services.AddServal().AddHttpListener(options =>
        {
            options.Prefixes.Clear();
            options.Prefixes.Add($"http://localhost:{port}/");
        });

        var configured = !string.IsNullOrWhiteSpace(host.Configuration["OAuth:ClientId"]);
        OAuthSample.Configure(serval, configured ? options => ConfigureProvider(options, host.Configuration, port) : null, true);
        return host.Build();
    }

    private static void ConfigureProvider(OAuthOptions options, IConfiguration configuration, int port)
    {
        options.ClientId = Required(configuration, "ClientId");

        var issuer = configuration["OAuth:Issuer"];
        if (!string.IsNullOrWhiteSpace(issuer))
        {
            options.OpenIdConnect = new OpenIdConnectOptions
            {
                Issuer = issuer
            };
        }
        else
        {
            options.AuthorizationEndpoint   = new Uri(Required(configuration, "AuthorizationEndpoint"));
            options.TokenEndpoint           = new Uri(Required(configuration, "TokenEndpoint"));
            options.UserInformationEndpoint = new Uri(Required(configuration, "UserInformationEndpoint"));
        }

        options.RedirectUri             = new Uri($"http://localhost:{port}/auth/callback");
        options.AllowLoopbackHttp       = true;
        options.CorrelationCookiePrefix = "Serval.Sample.OAuth.State.";
        options.CorrelationCookie = new CookieOptions
        {
            Secure = false
        };
        options.ExpectedIssuer = configuration["OAuth:ExpectedIssuer"];
        options.ClientSecret   = configuration["OAuth:ClientSecret"];
        options.ClientAuthentication = options.ClientSecret is null
            ? OAuthClientAuthentication.None
            : configuration.GetValue("OAuth:ClientAuthentication", OAuthClientAuthentication.ClientSecretBasic);
        foreach (var scope in (configuration["OAuth:Scopes"] ?? "profile").Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            options.Scopes.Add(scope);
        }

        if (options.OpenIdConnect is not null)
        {
            return;
        }

        var idClaim   = configuration["OAuth:IdClaim"]   ?? "sub";
        var nameClaim = configuration["OAuth:NameClaim"] ?? "name";
        options.CreatePrincipal = async signIn =>
        {
            var profile = await signIn.Client.GetUserInformationAsync(signIn.Tokens, signIn.Request.RequestAborted);
            if (!profile.TryGetProperty(idClaim, out var id) || id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString()))
            {
                throw new OAuthProtocolException("missing_subject");
            }

            var subject = id.GetString()!;
            var name    = profile.TryGetProperty(nameClaim, out var display) && display.ValueKind == JsonValueKind.String ? display.GetString() : null;
            // Use a stable provider subject for account identity. Never link accounts
            // solely by a display name or by an unverified email address.
            return new ClaimsPrincipal(new ClaimsIdentity([
                new Claim(ClaimTypes.NameIdentifier, subject, ClaimValueTypes.String, "provider"),
                new Claim(ClaimTypes.Name, name ?? subject),
            ], "OAuth"));
        };
    }

    private static string Required(IConfiguration configuration, string name)
    {
        var value = configuration["OAuth:" + name];
        return string.IsNullOrWhiteSpace(value) ? throw new InvalidOperationException($"Set OAuth__{name} before starting the OAuth sample.") : value;
    }
}
