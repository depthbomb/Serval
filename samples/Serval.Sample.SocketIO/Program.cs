#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Serval.Generated;
using Serval.Hosting;
using Serval.SocketIO;
using Serval.Transport.HttpListener;

namespace Serval.Sample.SocketIO;

public static class Program
{
    public static async Task Main(string[] args)
    {
        using var host = Build(args);
        await host.RunAsync();
    }

    public static IHost Build(string[] args)
    {
        var host = Host.CreateApplicationBuilder(args);
        var port = host.Configuration.GetValue("port", 8119);
        host.Services.AddServal(options =>
        {
            options.StaticFiles.DirectoryPath = Path.Combine(AppContext.BaseDirectory, "Public");
        }).AddGeneratedControllers().AddSocketIO(socketIO =>
        {
            socketIO.MapNamespace<ChatHandler>("/").MapNamespace<ChatHandler>("/chat");
        }).AddHttpListener(options =>
        {
            options.Prefixes.Clear();
            options.Prefixes.Add($"http://localhost:{port}/");
        });

        return host.Build();
    }
}
