#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Serval.Configuration;

namespace Serval.Sample.Configuration;

public static class Program
{
    public static async Task Main(string[] args)
    {
        // The committed example is safe to run directly. Real apps normally use the default .env path.
        var builder = Host.CreateApplicationBuilder(args);
        builder.UseDotEnvConfiguration(new DotEnvConfigurationOptions
        {
            Path                = Path.Combine(AppContext.BaseDirectory, ".env.example"),
            Optional            = false,
            ReloadOnChange      = false,
            LoadEnvironmentFile = false,
            KeyMappings =
            {
                ["APP_NAME"] = "Server:Name",
                ["APP_PORT"] = "Server:Port",
            },
        });

        builder.Services.AddSingleton<IValidateOptions<ServerOptions>, ServerOptionsValidator>();
        builder.Services.AddOptions<ServerOptions>().BindConfiguration("Server").ValidateOnStart();

        using var host = builder.Build();
        await host.StartAsync();
        var options = host.Services.GetRequiredService<IOptions<ServerOptions>>().Value;
        Console.WriteLine($"{options.Name}: port {options.Port}, diagnostics {options.Diagnostics}");
        await host.StopAsync();
    }
}
