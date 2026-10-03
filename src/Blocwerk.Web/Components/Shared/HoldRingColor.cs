// <copyright file="HoldRingColor.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.RegularExpressions;
using Blocwerk.Core.Holds;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// The <c>--ring</c> custom property a hold thumbnail is outlined in, so a pair reads at a glance.
/// <see cref="Core.Entities.Hold.Color"/> is free text on the way in, and this value lands inside an
/// inline <c>style</c>: only a palette key (mapped through <see cref="HoldPalette"/>) or a strict hex
/// colour is ever emitted — anything else keeps the default ring.
/// </summary>
public static partial class HoldRingColor
{
    /// <summary>The inline style for <paramref name="color"/>, or empty for the default ring.</summary>
    /// <param name="color">The hold's stored colour: a palette key such as "red", or a hex value.</param>
    /// <returns><c>--ring:#rrggbb</c> or an empty string.</returns>
    public static string Style(string? color)
    {
        if (string.IsNullOrEmpty(color))
        {
            return string.Empty;
        }

        if (HoldPalette.All.Any(c => c.Key == color))
        {
            return $"--ring:{HoldPalette.Hex(color)}";
        }

        return HexColor().IsMatch(color) ? $"--ring:{color}" : string.Empty;
    }

    [GeneratedRegex("^#[0-9a-fA-F]{3,8}$")]
    private static partial Regex HexColor();
}
