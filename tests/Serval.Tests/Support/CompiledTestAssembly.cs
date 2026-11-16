#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.CodeAnalysis;
using System.Reflection;
using System.Runtime.Loader;

namespace Serval.Tests.Support;

internal sealed class CompiledTestAssembly : IDisposable
{
    public           Assembly            Assembly { get; }
    private readonly AssemblyLoadContext _context;

    public CompiledTestAssembly(Compilation compilation)
    {
        using var stream = new MemoryStream();
        var       emit   = compilation.Emit(stream);
        Assert.True(emit.Success, string.Join(Environment.NewLine, emit.Diagnostics));
        stream.Position = 0;
        _context        = new AssemblyLoadContext(compilation.AssemblyName!, true);
        try
        {
            Assembly = _context.LoadFromStream(stream);
        }
        catch
        {
            _context.Unload();
            throw;
        }
    }

    public void Dispose() => _context.Unload();
}
