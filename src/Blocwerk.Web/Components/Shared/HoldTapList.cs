// <copyright file="HoldTapList.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.RegularExpressions;
using Blocwerk.Core.Detection.Outlines;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Holds;
using Blocwerk.Core.HoldStacks;
using Blocwerk.Core.Services;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// The compact hold list behind the wall view's outlines on demand: the holds of one panel that can be tapped, with
/// what hold-tap.js needs to hit-test them and to draw the tapped one. A hold the last update did not find is left
/// out, so it can be neither tapped nor outlined. A hold that sits on another one says so (<see cref="HoldTapItem.On"/>):
/// the browser then meets the upper hold first under a tap, outlines what sits on the tapped hold darker and the hold
/// it sits on subdued. The overlap of two holds that are not stacked is still told by their outlines.
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
        var position = shown.Select((h, i) => (h.Id, i)).ToDictionary(p => p.Id, p => p.i);
        var on = holds.GroupBy(h => h.Id).ToDictionary(g => g.Key, g => g.First().StackedOn);
        return [.. shown.Select((h, i) => Item(h, Unstacked(overlaps[i], i, shown, on), Base(h, on, position), usage))];
    }

    /// <summary>The nearest hold below <paramref name="hold"/> that is in the list: a base that is not shown is skipped.</summary>
    private static int? Base(PanelHold hold, Dictionary<Guid, Guid?> on, Dictionary<Guid, int> position) =>
        HoldStackOrder.Below(on, hold.Id).Where(position.ContainsKey).Select(id => (int?)position[id]).FirstOrDefault();

    /// <summary>The overlapping holds that are not above or below the hold itself: those are told by the stack.</summary>
    private static IReadOnlyList<int> Unstacked(IReadOnlyList<int> overlaps, int self, List<PanelHold> shown, Dictionary<Guid, Guid?> on)
    {
        if (overlaps.Count == 0)
        {
            return overlaps;
        }

        var below = HoldStackOrder.Below(on, shown[self].Id);
        return [.. overlaps.Where(o => !below.Contains(shown[o].Id) && !HoldStackOrder.Below(on, shown[o].Id).Contains(shown[self].Id))];
    }

    private static HoldTapItem Item(PanelHold hold, IReadOnlyList<int> overlaps, int? on, IReadOnlyDictionary<Guid, int>? usage)
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
            overlaps,
            on);
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
