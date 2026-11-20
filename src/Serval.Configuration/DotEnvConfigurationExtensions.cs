#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.Hosting;

namespace Serval.Configuration;

/// <summary>Adds dotenv files and replaces a host's default appsettings sources.</summary>
public static class DotEnvConfigurationExtensions
{
    extension(IConfigurationBuilder builder)
    {
        /// <summary>Adds a dotenv file at the end of the configuration sources.</summary>
        /// <remarks>Later sources override earlier ones. Double underscores in keys become section separators.</remarks>
        public IConfigurationBuilder AddDotEnvFile(string path, bool optional = false, bool reloadOnChange = false)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);

            return builder.AddDotEnvFile(source =>
            {
                source.Path           = path;
                source.Optional       = optional;
                source.ReloadOnChange = reloadOnChange;
            });
        }

        /// <summary>Adds a dotenv file with custom file settings and conventional-key mappings.</summary>
        public IConfigurationBuilder AddDotEnvFile(Action<DotEnvConfigurationSource> configure)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(configure);

            var source = new DotEnvConfigurationSource();
            configure(source);

            ArgumentException.ThrowIfNullOrWhiteSpace(source.Path);

            source.ResolveFileProvider();
            return builder.Add(source);
        }
    }

    extension<TBuilder>(TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        /// <summary>Replaces default appsettings files with .env and .env.EnvironmentName, retaining other sources.</summary>
        /// <remarks>
        /// Call once, immediately after creating the host builder and before reading configuration or registering services.
        /// Mappings apply independently to the files and existing sources, including environment variables and arguments.
        /// Explicit destination keys win over mapped names within a source; source precedence is preserved.
        /// Host bootstrap settings, including EnvironmentName and ContentRootPath, have already been selected.
        /// </remarks>
        public TBuilder UseDotEnvConfiguration(DotEnvConfigurationOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(builder);

            options ??= new DotEnvConfigurationOptions();

            ArgumentException.ThrowIfNullOrWhiteSpace(options.Path);

            var mappings = DotEnvKeyMappings.Create(options.KeyMappings);
            var sources  = builder.Configuration.Sources.ToArray();
            if (sources.OfType<DotEnvConfigurationSource>().Any(source => source.IsHostDefault))
            {
                throw new InvalidOperationException("Dotenv host configuration has already been registered.");
            }

            var environment = builder.Environment.EnvironmentName;
            if (options.LoadEnvironmentFile && (string.IsNullOrWhiteSpace(environment) || environment.IndexOfAny(['/', '\\']) >= 0))
            {
                throw new ArgumentException("The environment name must be a nonempty file-name suffix.", nameof(builder));
            }

            var files = new List<DotEnvConfigurationSource>
            {
                CreateSource(builder, options, options.Path, options.Optional, mappings),
            };
            if (options.LoadEnvironmentFile)
            {
                files.Add(CreateSource(builder, options, $"{options.Path}.{environment}", true, mappings));
            }

            var insertionIndex = Array.FindIndex(sources, source => IsAppSettings(source, environment));
            if (insertionIndex < 0)
            {
                insertionIndex = 0;
            }

            try
            {
                // Load both files before changing the live host. Prepared providers are transferred on Add.
                foreach (var file in files)
                {
                    file.Prepare(builder.Configuration, mappings);
                }

                var providers = ((IConfigurationRoot)builder.Configuration).Providers.ToArray();
                for (var index = 0; index < sources.Length; index++)
                {
                    if (sources[index] is MemoryConfigurationSource && providers[index] is MemoryConfigurationProvider memory)
                    {
                        sources[index] = new MemoryConfigurationSource { InitialData = memory.ToArray() };
                    }
                }

                var mappedKeys = mappings.Count == 0 ? null : new MappedConfigurationKeys(mappings);
                builder.Configuration.Sources.Clear();
                for (var index = 0; index <= sources.Length; index++)
                {
                    if (index == insertionIndex)
                    {
                        foreach (var file in files)
                        {
                            builder.Configuration.Sources.Add(file);
                        }
                    }

                    if (index < sources.Length && !IsAppSettings(sources[index], environment))
                    {
                        var source = sources[index];
                        builder.Configuration.Sources.Add(mappedKeys is null ? source : new MappedConfigurationSource(source, mappedKeys));
                    }
                }
            }
            finally
            {
                foreach (var file in files)
                {
                    file.DisposePreparedProvider();
                }
            }

            return builder;
        }
    }

    private static bool IsAppSettings(IConfigurationSource source, string environment)
    {
        return source is JsonConfigurationSource json &&
               (string.Equals(json.Path, "appsettings.json", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(json.Path, $"appsettings.{environment}.json", StringComparison.OrdinalIgnoreCase));
    }

    private static DotEnvConfigurationSource CreateSource(IHostApplicationBuilder             builder,
                                                          DotEnvConfigurationOptions          options,
                                                          string                              path,
                                                          bool                                optional,
                                                          IReadOnlyDictionary<string, string> mappings)
    {
        var source = new DotEnvConfigurationSource
        {
            Path           = path,
            Optional       = optional,
            ReloadOnChange = options.ReloadOnChange,
            IsHostDefault  = true,
        };
        foreach (var (destination, alias) in mappings)
        {
            source.KeyMappings.Add(alias, destination);
        }

        source.ResolveFileProvider();
        source.FileProvider ??= builder.Environment.ContentRootFileProvider;
        return source;
    }
}
