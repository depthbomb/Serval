#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;

namespace Serval.Configuration;

internal sealed class MappedConfigurationSource : IConfigurationSource
{
    private readonly IConfigurationSource    _source;
    private readonly MappedConfigurationKeys _mappings;

    internal MappedConfigurationSource(IConfigurationSource source, MappedConfigurationKeys mappings)
    {
        _source   = source;
        _mappings = mappings;
    }

    public IConfigurationProvider Build(IConfigurationBuilder builder)
        => new MappedConfigurationProvider(_source.Build(builder), _mappings);
}

internal sealed class MappedConfigurationProvider : IConfigurationProvider, IDisposable
{
    private readonly IConfigurationProvider              _provider;
    private readonly MappedConfigurationKeys             _mappings;
    private readonly IReadOnlyDictionary<string, string> _aliases;

    internal MappedConfigurationProvider(IConfigurationProvider provider, MappedConfigurationKeys mappings)
    {
        _provider = provider;
        _mappings = mappings;
        _aliases  = mappings.Aliases;
    }

    public bool TryGet(string key, out string? value)
    {
        if (_provider.TryGet(key, out value))
        {
            return true;
        }

        return _aliases.TryGetValue(key, out var alias) && _provider.TryGet(alias, out value);
    }

    public void Set(string key, string? value) => _provider.Set(key, value);

    public IChangeToken GetReloadToken() => _provider.GetReloadToken();

    public void Load() => _provider.Load();

    public IEnumerable<string> GetChildKeys(IEnumerable<string> earlierKeys, string? parentPath)
    {
        return _provider.GetChildKeys(_mappings.AddChildren(_provider, earlierKeys, parentPath), parentPath);
    }

    public void Dispose()
    {
        if (_provider is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}
