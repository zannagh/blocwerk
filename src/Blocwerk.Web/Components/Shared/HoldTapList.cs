// <copyright file="HoldTapList.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.RegularExpressions;
using Blocwerk.Core.Detection.Outlines;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Holds;
using Blocwerk.Core.Services;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// The compact hold list behind the wall view's outlines on demand: the holds of one panel that can be tapped, with
/// what hold-tap.js needs to hit-test them and to draw the tapped one. A hold the last update did not find is left
/// out, so it can be neither tapped nor outlined.
/// </summary>
public static partial class HoldTapList
{
    /// <summary>Coordinates are whole numbers of this many parts of the photo's width or height.</summary>
    public const int Scale = 10000;

    /// <summary>Builds the list for one panel.</summary>
    /// <param name="holds">The panel's holds, in its photo's normalized space.</param>
    /// <param name="usage">Boulders per hold id, or null when not known (the label then leaves the count out).</param>
    /// <returns>The tappable holds; <see cref="HoldTapItem.Overlaps"/> refers to positions in this list.</returns>
    public static List<HoldTapItem> Build(IReadOnlyList<PanelHold> holds, IReadOnlyDictionary<Guid, int>? usage = null)
    {
        ArgumentNullException.ThrowIfNull(holds);
        var shown = holds.Where(h => !h.Kept).ToList();
        var overlaps = HoldOutlineOverlap.Find(shown);
        return [.. shown.Select((h, i) => Item(h, overlaps[i], usage))];
    }

    private static HoldTapItem Item(PanelHold hold, IReadOnlyList<int> overlaps, IReadOnlyDictionary<Guid, int>? usage)
    {
        bool shaped = hold.ShapePoints is { Count: >= 3 };
        var holes = shaped ? hold.ShapeHoles?.Where(ring => ring is { Count: >= 3 }).Select(ring => Ring(hold, ring)).ToArray() : null;
        var color = ColorHex(hold.Color);
        return new HoldTapItem(
            Units(hold.X),
            Units(hold.Y),
            Units(hold.Radius),
            shaped ? Ring(hold, hold.ShapePoints!) : null,
            holes is { Length: > 0 } ? holes : null,
            hold.Category == HoldCategory.Foot,
            color,
            IsKnown(hold.Color) ? HoldPalette.DisplayName(hold.Color) : null,
            usage is null ? null : usage.GetValueOrDefault(hold.Id),
            overlaps);
    }

    private static int[] Ring(PanelHold hold, List<ShapePoint> ring) =>
        [.. ring.SelectMany(p => new[] { Units(hold.X + p.Dx), Units(hold.Y + p.Dy) })];

    private static int Units(double normalized) => (int)Math.Round(normalized * Scale);

    private static bool IsKnown(string? color) =>
        !string.IsNullOrWhiteSpace(color) && !ReferenceEquals(HoldPalette.Get(color), HoldPalette.Unknown);

    private static string? ColorHex(string? color)
    {
        if (IsKnown(color))
        {
            return HoldPalette.Hex(color);
        }

        return color is not null && HexColor().IsMatch(color) ? color : null;
    }

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex HexColor();
}
