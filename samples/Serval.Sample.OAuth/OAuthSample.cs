#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.Extensions.DependencyInjection;
using Serval.Authentication;
using Serval.Generated;
using Serval.Hosting;
using Serval.Http;
using Serval.OAuth;

namespace Serval.Sample.OAuth;

public sealed record ProviderStatus(bool Configured);

public static class OAuthSample
{
    public static void Configure(ServalBuilder builder, Action<OAuthOptions>? configureProvider, bool loopbackHttp = false)
    {
        builder.Services.AddSingleton(new ProviderStatus(configureProvider is not null));

        var authentication = builder
                             .AddGeneratedControllers()
                             .AddAuthentication(options => options.DefaultScheme = "Cookies").AddCookie(configure: options =>
                             {
                                 if (loopbackHttp)
                                 {
                                     options.CookieName = "Serval.Sample.OAuth.Session";
                                     options.Cookie = new CookieOptions
                                     {
                                         Secure = false,
                                     };
                                 }
                             });
        if (configureProvider is not null)
        {
            authentication.AddOAuth("provider", configureProvider);
        }
    }
}
