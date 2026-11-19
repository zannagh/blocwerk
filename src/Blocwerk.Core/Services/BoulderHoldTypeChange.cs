// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Enums;
using Blocwerk.Core.Helpers;

namespace Blocwerk.Core.Services;

/// <summary>
/// A change of a boulder's dominant hold type within a revision. The revision's argument is nullable: null leaves the
/// type as stored, a change with a null <paramref name="Type"/> clears it, so the holds decide again.
/// </summary>
/// <param name="Type">The new type, or null to clear it.</param>
public readonly record struct BoulderHoldTypeChange(HoldHandType? Type)
{
    /// <summary>
    /// Reads the form value the editor and the offline queue send: absent (null) leaves the type alone, "" clears it, a
    /// type's name sets it. A value that names no type leaves it alone too, so a stray value never clears a type.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <returns>The change, or null to leave the type alone.</returns>
    public static BoulderHoldTypeChange? FromForm(string? value) =>
        value is null ? null
        : value.Length == 0 ? new BoulderHoldTypeChange(null)
        : BoulderHoldTypes.FromSlug(value) is { } type ? new BoulderHoldTypeChange(type)
        : null;

    /// <summary>The form value of a type: its name, or "" for none.</summary>
    /// <param name="type">The type, or null.</param>
    /// <returns>The value.</returns>
    public static string ToForm(HoldHandType? type) => type is { } t ? BoulderHoldTypes.Label(t) : string.Empty;
}
