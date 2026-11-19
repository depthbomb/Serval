#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

namespace Serval.Savanna;

/// <summary>Writes a named fragment into the current render.</summary>
/// <param name="writer">The shared output writer.</param>
/// <param name="context">The active render context.</param>
/// <returns>An operation that completes when the fragment has been written.</returns>
public delegate ValueTask SavannaRenderFragment(SavannaWriter writer, SavannaRenderContext context);

/// <summary>Holds a copied, case-sensitive mapping of slot names to render fragments.</summary>
public sealed class SavannaSlots
{
    /// <summary>The shared slot set with no fragments.</summary>
    public static SavannaSlots Empty { get; } = new(new Dictionary<string, SavannaRenderFragment>());

    private readonly Dictionary<string, SavannaRenderFragment> _slots;

    /// <summary>Copies named fragments into a slot set.</summary>
    /// <param name="slots">The case-sensitive names and their render delegates.</param>
    /// <exception cref="ArgumentNullException">The mapping is null.</exception>
    public SavannaSlots(IReadOnlyDictionary<string, SavannaRenderFragment> slots)
    {
        ArgumentNullException.ThrowIfNull(slots);
        _slots = new Dictionary<string, SavannaRenderFragment>(slots, StringComparer.Ordinal);
    }

    /// <summary>Checks whether a named fragment was supplied.</summary>
    /// <param name="name">The case-sensitive slot name.</param>
    /// <returns>True when the slot exists.</returns>
    public bool Contains(string name) => _slots.ContainsKey(name);

    /// <summary>Renders a supplied slot, or skips an absent optional slot.</summary>
    /// <param name="name">The case-sensitive slot name.</param>
    /// <param name="writer">The shared output writer.</param>
    /// <param name="context">The active render context.</param>
    /// <param name="required">Whether an absent slot is an error; defaults to false.</param>
    /// <returns>
    ///     An operation that completes when the fragment finishes, or immediately for an absent optional slot.
    /// </returns>
    /// <exception cref="InvalidOperationException">A required slot is absent.</exception>
    /// <exception cref="OperationCanceledException">The render token is canceled.</exception>
    public ValueTask RenderAsync(string name, SavannaWriter writer, SavannaRenderContext context, bool required = false)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        if (_slots.TryGetValue(name, out var fragment))
        {
            return fragment(writer, context);
        }

        return required ? throw new InvalidOperationException("A required Savanna slot was not supplied: " + name) : ValueTask.CompletedTask;
    }
}
