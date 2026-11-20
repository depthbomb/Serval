#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.Extensions.Configuration;

namespace Serval.Configuration;

internal sealed class MappedConfigurationKeys
{
    internal IReadOnlyDictionary<string, string> Aliases { get; }

    private readonly Dictionary<string, KeyValuePair<string, string>[]> _children = new(StringComparer.OrdinalIgnoreCase);

    internal MappedConfigurationKeys(IReadOnlyDictionary<string, string> aliases)
    {
        Aliases = aliases;
        var sections = new Dictionary<string, List<KeyValuePair<string, string>>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (destination, alias) in aliases)
        {
            var start = 0;
            while (start < destination.Length)
            {
                var end = destination.IndexOf(':', start);
                if (end < 0)
                {
                    end = destination.Length;
                }

                var parent = start == 0 ? string.Empty : destination[..(start - 1)];
                var child  = destination[start..end];
                if (!sections.TryGetValue(parent, out var children))
                {
                    children = [];
                    sections.Add(parent, children);
                }

                children.Add(new KeyValuePair<string, string>(child, alias));
                start = end + 1;
            }
        }

        foreach (var (parent, children) in sections)
        {
            // Group repeated child names cheaply; the wrapped provider applies configuration-key ordering.
            children.Sort(static (left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.Key, right.Key));
            _children.Add(parent, children.ToArray());
        }
    }

    internal IEnumerable<string> AddChildren(IConfigurationProvider provider, IEnumerable<string> earlierKeys, string? parentPath)
    {
        if (!_children.TryGetValue(parentPath ?? string.Empty, out var children))
        {
            return earlierKeys;
        }

        return earlierKeys.Concat(GetPresentChildren(provider, children));
    }

    private static IEnumerable<string> GetPresentChildren(IConfigurationProvider provider, KeyValuePair<string, string>[] children)
    {
        string? previousChild = null;
        foreach (var (child, alias) in children)
        {
            if (!string.Equals(child, previousChild, StringComparison.OrdinalIgnoreCase) && provider.TryGet(alias, out _))
            {
                previousChild = child;
                yield return child;
            }
        }
    }
}
