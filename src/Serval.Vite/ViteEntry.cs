#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

namespace Serval.Vite;

/// <summary>The URLs needed to load one Vite entry point and its static dependencies.</summary>
/// <remarks>
///     Generated lookups create these once, so requests do not parse JSON or walk the dependency graph.
///     URLs are raw strings; let Savanna or your HTML encoder escape them when writing attributes.
///     Dynamic imports stay lazy and are not included unless they are also statically imported.
///     In development builds, scripts load the Vite client and the entry source. Vite handles imported styles and
///     dependencies, so stylesheet and preload lists are empty. Render these scripts as modules to enable HMR.
/// </remarks>
public sealed class ViteEntry
{
    /// <summary>Module script URLs in load order, including the Vite client in development builds.</summary>
    /// <remarks>
    ///     Production CSS entries have no scripts. Development CSS entries load as modules so Vite can update their
    ///     styles.
    /// </remarks>
    public IReadOnlyList<string> Scripts { get; }

    /// <summary>Deduplicated stylesheets, with entry CSS first and imported CSS in dependency order.</summary>
    /// <remarks>Includes Vite's global style.css output when CSS splitting is disabled.</remarks>
    public IReadOnlyList<string> Stylesheets { get; }

    /// <summary>Deduplicated static script dependencies in dependency order, excluding the entry itself.</summary>
    /// <remarks>These URLs can be rendered as link elements with rel="modulepreload".</remarks>
    public IReadOnlyList<string> ModulePreloads { get; }

    /// <summary>Creates an immutable snapshot of an entry's precomputed URLs.</summary>
    /// <param name="scripts">Module script URLs in rendering order.</param>
    /// <param name="stylesheets">Stylesheet URLs in rendering order.</param>
    /// <param name="modulePreloads">Static module dependency URLs in dependency order.</param>
    /// <remarks>Copies the supplied sequences; later caller changes cannot affect the entry.</remarks>
    /// <exception cref="ArgumentNullException">A sequence is null.</exception>
    public ViteEntry(IEnumerable<string> scripts, IEnumerable<string> stylesheets, IEnumerable<string> modulePreloads)
    {
        ArgumentNullException.ThrowIfNull(scripts);
        ArgumentNullException.ThrowIfNull(stylesheets);
        ArgumentNullException.ThrowIfNull(modulePreloads);

        Scripts        = Array.AsReadOnly<string>([.. scripts]);
        Stylesheets    = Array.AsReadOnly<string>([.. stylesheets]);
        ModulePreloads = Array.AsReadOnly<string>([.. modulePreloads]);
    }
}
