#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.Extensions.Configuration;

namespace Serval.Configuration;

/// <summary>Describes a dotenv file and its optional conventional-key mappings.</summary>
public sealed class DotEnvConfigurationSource : FileConfigurationSource
{
    /// <summary>Maps conventional names to configuration paths. Mappings are copied when the provider is built.</summary>
    public IDictionary<string, string> KeyMappings { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    internal bool IsHostDefault { get; init; }

    private DotEnvConfigurationProvider? _preparedProvider;

    /// <inheritdoc/>
    public override IConfigurationProvider Build(IConfigurationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(Path);

        if (_preparedProvider is { } prepared)
        {
            _preparedProvider = null;
            return prepared;
        }

        ResolveFileProvider();
        EnsureDefaults(builder);

        return new DotEnvConfigurationProvider(this);
    }

    internal void Prepare(IConfigurationBuilder builder, IReadOnlyDictionary<string, string> mappings)
    {
        ResolveFileProvider();
        EnsureDefaults(builder);

        var provider = new DotEnvConfigurationProvider(this, mappings);
        try
        {
            provider.Prepare();
            _preparedProvider = provider;
        }
        catch
        {
            provider.Dispose();
            throw;
        }
    }

    internal void DisposePreparedProvider()
    {
        _preparedProvider?.Dispose();
        _preparedProvider = null;
    }
}
