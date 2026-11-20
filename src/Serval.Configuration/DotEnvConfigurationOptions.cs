#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

namespace Serval.Configuration;

/// <summary>Configures dotenv files that replace a host's default appsettings files.</summary>
public sealed class DotEnvConfigurationOptions
{
    /// <summary>The base file, relative to the host's content root unless absolute. Defaults to .env.</summary>
    public string Path { get; init; } = ".env";

    /// <summary>Whether the base file may be missing. Defaults to true for environment-only deployments.</summary>
    public bool Optional { get; init; } = true;

    /// <summary>Whether changes to the files reload configuration. Defaults to true.</summary>
    public bool ReloadOnChange { get; init; } = true;

    /// <summary>Whether to also load the optional Path.EnvironmentName file. Defaults to true.</summary>
    public bool LoadEnvironmentFile { get; init; } = true;

    /// <summary>Maps conventional names, such as APP_PORT, to configuration paths, such as Server:Port.</summary>
    /// <remarks>Mappings apply within each existing source. An explicit destination key wins in the same source.</remarks>
    public IDictionary<string, string> KeyMappings { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}
