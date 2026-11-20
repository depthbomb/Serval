#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.FileProviders.Physical;

namespace Serval.Configuration;

/// <summary>Loads dotenv values into configuration without changing process environment variables.</summary>
public sealed class DotEnvConfigurationProvider : FileConfigurationProvider
{
    private bool             _prepared;
    private HashSet<string>? _explicitMappedKeys;

    private readonly IReadOnlyDictionary<string, string> _mappings;
    private readonly IDisposable?                        _ownedFileProvider;

    /// <summary>Creates a provider for a dotenv file.</summary>
    public DotEnvConfigurationProvider(DotEnvConfigurationSource source) : this(source, GetMappings(source)) {}

    internal DotEnvConfigurationProvider(DotEnvConfigurationSource           source,
                                         IReadOnlyDictionary<string, string> mappings) : base(CreateFileSource(source))
    {
        _mappings          = mappings;
        _ownedFileProvider = ReferenceEquals(source.FileProvider, Source.FileProvider) ? null : Source.FileProvider as IDisposable;
    }

    /// <inheritdoc/>
    public override void Load()
    {
        if (_prepared)
        {
            _prepared = false;
            return;
        }

        base.Load();
    }

    /// <inheritdoc/>
    public override void Load(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var              data               = DotEnvParser.Parse(stream);
        HashSet<string>? explicitMappedKeys = null;
        foreach (var (destination, alias) in _mappings)
        {
            if (data.ContainsKey(destination))
            {
                explicitMappedKeys ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                explicitMappedKeys.Add(destination);
            }

            if (data.TryGetValue(alias, out var value))
            {
                data.TryAdd(destination, value);
            }
        }

        Data                = data;
        _explicitMappedKeys = explicitMappedKeys;
    }

    /// <inheritdoc/>
    public override void Set(string key, string? value)
    {
        base.Set(key, value);
        if (_mappings.ContainsKey(key))
        {
            _explicitMappedKeys ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _explicitMappedKeys.Add(key);
            return;
        }

        foreach (var (destination, alias) in _mappings)
        {
            if (!string.Equals(alias, key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!Data.ContainsKey(destination))
            {
                _explicitMappedKeys?.Remove(destination);
            }

            if (_explicitMappedKeys?.Contains(destination) != true)
            {
                base.Set(destination, value);
            }
            break;
        }
    }

    internal void Prepare()
    {
        base.Load();
        _prepared = true;
    }

    private static IReadOnlyDictionary<string, string> GetMappings(DotEnvConfigurationSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return DotEnvKeyMappings.Create(source.KeyMappings);
    }

    private static DotEnvConfigurationSource CreateFileSource(DotEnvConfigurationSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(source.Path);

        if (source.FileProvider is not PhysicalFileProvider physical || physical.GetType() != typeof(PhysicalFileProvider))
        {
            return source;
        }

        // PhysicalFileProvider normally hides dot-prefixed files, including .env.
        // Give this provider its own file watcher so the host's shared provider stays unchanged.
        return new DotEnvConfigurationSource
        {
            Path            = source.Path,
            Optional        = source.Optional,
            ReloadOnChange  = source.ReloadOnChange,
            ReloadDelay     = source.ReloadDelay,
            OnLoadException = source.OnLoadException,
            FileProvider = new PhysicalFileProvider(physical.Root, ExclusionFilters.None)
            {
                UsePollingFileWatcher = physical.UsePollingFileWatcher,
                UseActivePolling      = physical.UseActivePolling,
            },
        };
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _ownedFileProvider?.Dispose();
        }
    }
}
