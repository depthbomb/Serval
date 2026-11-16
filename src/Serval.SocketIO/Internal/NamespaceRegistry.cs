#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

namespace Serval.SocketIO.Internal;

internal sealed class NamespaceRegistry
{
    internal IReadOnlyDictionary<string, Type> Handlers { get; }

    internal NamespaceRegistry(Dictionary<string, Type> handlers)
    {
        Handlers = new Dictionary<string, Type>(handlers, StringComparer.Ordinal);
    }
}
