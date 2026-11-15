#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.CodeAnalysis;
using System.Reflection;
using System.Runtime.Loader;

namespace Serval.Savanna.LanguageServer.Workspace;

// Keep the project's build outputs writable while the editor holds generator instances.
internal sealed class EditorAnalyzerLoader : IAnalyzerAssemblyLoader, IDisposable
{
    private readonly Dictionary<string, Assembly> _assemblies   = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<GeneratorContext>       _contexts     = [];
    private readonly Dictionary<string, string>   _dependencies = new(StringComparer.OrdinalIgnoreCase);

    public void AddDependencyLocation(string fullPath)
    {
        lock (_assemblies)
        {
            _dependencies[Path.GetFileNameWithoutExtension(fullPath)] = fullPath;
        }
    }

    public Assembly LoadFromPath(string fullPath)
    {
        lock (_assemblies)
        {
            if (!_assemblies.TryGetValue(fullPath, out var assembly))
            {
                var context = new GeneratorContext(Path.GetDirectoryName(fullPath)!, new Dictionary<string, string>(_dependencies, StringComparer.OrdinalIgnoreCase));
                _contexts.Add(context);
                assembly = context.LoadFile(fullPath);
                _assemblies.Add(fullPath, assembly);
            }

            return assembly;
        }
    }

    public void Dispose()
    {
        lock (_assemblies)
        {
            _assemblies.Clear();
            _dependencies.Clear();
            foreach (var context in _contexts)
            {
                context.Unload();
            }

            _contexts.Clear();
        }
    }

    private sealed class GeneratorContext : AssemblyLoadContext
    {
        private readonly IReadOnlyDictionary<string, string> _dependencies;
        private readonly string                              _directory;

        public GeneratorContext(string directory, IReadOnlyDictionary<string, string> dependencies) : base(true)
        {
            _directory    = directory;
            _dependencies = dependencies;
        }

        public Assembly LoadFile(string path)
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return LoadFromStream(file);
        }

        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name is null                                                              || name.Name.StartsWith("Microsoft.CodeAnalysis", StringComparison.Ordinal)
                                                                                               || name.Name.StartsWith("System.", StringComparison.Ordinal) || name.Name == "netstandard")
            {
                return null;
            }

            var adjacent = Path.Combine(_directory, name.Name + ".dll");
            if (File.Exists(adjacent))
            {
                return LoadFile(adjacent);
            }

            return _dependencies.TryGetValue(name.Name, out var path) && File.Exists(path) ? LoadFile(path) : null;
        }
    }
}
