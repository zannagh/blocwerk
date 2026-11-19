// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Enums;
using Blocwerk.Core.Helpers;

namespace Blocwerk.Core.Services;

/// <summary>
/// A change of a boulder's hold types within a revision. The revision's argument is nullable: null leaves the set as stored,
/// a change replaces the types within its <paramref name="Scope"/> with <paramref name="Types"/>, so a change with none
/// clears them and the holds decide again. Types outside the scope stay as stored.
/// </summary>
/// <param name="Types">The new types; <see cref="BoulderHoldType.None"/> clears them.</param>
/// <param name="Scope">The types the change speaks for: every type, or only the grips for a 0.7.0 client (<see cref="FromLegacy"/>).</param>
/// <param name="IsLegacy">True for the single type of a 0.7.0 client, which is no change while it is what that client was
/// shown (<see cref="ApplyTo"/>).</param>
public readonly record struct BoulderHoldTypeChange(BoulderHoldType Types, BoulderHoldType Scope, bool IsLegacy = false)
{
    /// <summary>Initializes a new instance of the <see cref="BoulderHoldTypeChange"/> struct that replaces the whole set.</summary>
    /// <param name="types">The new set; <see cref="BoulderHoldType.None"/> clears it.</param>
    public BoulderHoldTypeChange(BoulderHoldType types)
        : this(types, BoulderHoldTypes.Mask)
    {
    }

    /// <summary>
    /// The set after this change. A 0.7.0 page always sends its type, changed or not, and it showed the set's first grip
    /// (none when the set has no grip): a legacy value equal to that is no change, so a stale replay never collapses
    /// {Crimp, Pinch} to {Crimp} nor clears grips it was never shown.
    /// </summary>
    /// <param name="current">The set as stored.</param>
    /// <returns>The types outside the scope as stored, those within it as the change says.</returns>
    public BoulderHoldType ApplyTo(BoulderHoldType current) =>
        IsLegacy && Types == BoulderHoldTypes.FirstGrip(current) ? current : (current & ~Scope) | (Types & Scope);

    /// <summary>
    /// Reads the names the editor and the offline queue send: null (absent) leaves the set alone, an empty list clears it,
    /// names set it. A list that names no known type leaves it alone too, so stray values never clear a set.
    /// </summary>
    /// <param name="names">The type names (<c>Crimp</c>, <c>Heel hook</c>), or null.</param>
    /// <returns>The change, or null to leave the set alone.</returns>
    public static BoulderHoldTypeChange? FromNames(IReadOnlyCollection<string?>? names) =>
        names is null ? null
        : names.Count == 0 ? new BoulderHoldTypeChange(BoulderHoldType.None)
        : BoulderHoldTypes.FromNames(names) is var set && set != BoulderHoldType.None ? new BoulderHoldTypeChange(set)
        : null;

    /// <summary>
    /// Reads the single <c>holdType</c> a 0.7.0 editor or offline queue sends, which knows only the five grips: absent (null)
    /// leaves the set alone, "" clears the grips, a grip's name makes it the only grip. Either way the hooks, which that
    /// client never saw, stay as stored. A value that names no grip leaves the set alone, and one equal to what that client
    /// was shown is no change either (<see cref="ApplyTo"/>).
    /// </summary>
    /// <param name="value">The value.</param>
    /// <returns>The change, or null to leave the set alone.</returns>
    public static BoulderHoldTypeChange? FromLegacy(string? value) =>
        value is null ? null
        : value.Length == 0 ? new BoulderHoldTypeChange(BoulderHoldType.None, BoulderHoldTypes.Grips, IsLegacy: true)
        : BoulderHoldTypes.FromSlug(value) is { } type && (type & BoulderHoldTypes.Grips) != 0
            ? new BoulderHoldTypeChange(type, BoulderHoldTypes.Grips, IsLegacy: true)
        : null;

    /// <summary>The change a request carries: its <c>holdTypes</c> when present, else its old single <c>holdType</c>.</summary>
    /// <param name="names">The <c>holdTypes</c> list, or null when the request has none.</param>
    /// <param name="legacy">The <c>holdType</c> value of a 0.7.0 client, or null.</param>
    /// <returns>The change, or null to leave the set alone.</returns>
    public static BoulderHoldTypeChange? FromRequest(IReadOnlyCollection<string?>? names, string? legacy) =>
        names is null ? FromLegacy(legacy) : FromNames(names);

    /// <summary>The names a set goes over the wire as, in display order; empty for none.</summary>
    /// <param name="types">The set.</param>
    /// <returns>The names.</returns>
    public static IReadOnlyList<string> ToNames(BoulderHoldType types) => BoulderHoldTypes.ToNames(types);
}
