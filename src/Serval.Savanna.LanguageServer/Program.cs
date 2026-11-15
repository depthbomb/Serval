#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.Build.Locator;
using System.Runtime.CompilerServices;
using Serval.Savanna.LanguageServer.Rpc;
using Serval.Savanna.LanguageServer.Workspace;

namespace Serval.Savanna.LanguageServer;

public static class Program
{
    public static async Task<int> Main()
    {
        try
        {
            MSBuildLocator.RegisterDefaults();
            return await RunAsync();
        }
        catch (Exception error)
        {
            await Console.Error.WriteLineAsync(error.ToString());
            return 1;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<int> RunAsync()
    {
        using var workspace = new SavannaWorkspace();
        var       server    = new LanguageServer(new Protocol(Console.OpenStandardInput(), Console.OpenStandardOutput()), workspace);
        return await server.RunAsync();
    }
}
