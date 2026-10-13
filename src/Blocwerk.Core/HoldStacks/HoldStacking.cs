// <copyright file="HoldStacking.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.LookOver;

namespace Blocwerk.Core.HoldStacks;

/// <summary>
/// The one rule that says a hold sits ON another hold of the same photo (a hold on a volume): at least
/// <see cref="MinInside"/> of its area lies inside the other hold's outline, and the other hold is at least
/// <see cref="MinAreaRatio"/> times as large. Only a hold with a traced or drawn OUTLINE is taken for a base: a plain
/// circle (placed by hand, or a detection that could not be outlined) marks roughly where a hold is, and what lies in
/// it is usually that hold again or its neighbour. Pure: reads outlines, writes nothing.
/// </summary>
/// <remarks>
/// Both numbers and the outline rule were set on the real wall (the Attic, 1111 live holds, 2026-10-13). With 1.8 times
/// the area and circles as bases, 15 holds were stacked there and 9 of them wrongly: a second record of the same hold
/// (area ratios 2 to 3.5), or a neighbour that a coarse circle happens to cover. A hold that really sits on another one
/// is far smaller than it; from 4 times the area on, with an outlined base, none of the wrong ones is left. A person
/// can still stack any two holds in the editor.
/// </remarks>
public static class HoldStacking
{
    /// <summary>The share of the upper hold's area that must lie inside the base.</summary>
    public const double MinInside = 0.7;

    /// <summary>How many times larger (by area) the base must be.</summary>
    public const double MinAreaRatio = 4;

    /// <summary>Whether <paramref name="upper"/> sits on <paramref name="lower"/> by their outlines.</summary>
    /// <param name="upper">The hold that would be on top.</param>
    /// <param name="lower">The hold that would be its base.</param>
    /// <param name="size">The photo's pixel size (only circles need it); null treats it as square.</param>
    /// <returns>True when it does.</returns>
    public static bool SitsOn(Hold upper, Hold lower, (int Width, int Height)? size = null) =>
        MayCarry(lower) && SitsOn(Area(upper, size), Area(lower, size));

    /// <summary>
    /// Whether the two are a hold and its base, either way round: by the stored relation, or by their outlines. Such a
    /// pair is two holds, never the same hold twice and never one that replaced the other.
    /// </summary>
    /// <param name="a">One hold.</param>
    /// <param name="b">The other.</param>
    /// <param name="size">The photo's pixel size; null treats it as square.</param>
    /// <returns>True when one sits on the other.</returns>
    public static bool AreStack(Hold a, Hold b, (int Width, int Height)? size = null) =>
        a.StackedOnHoldId == b.Id || b.StackedOnHoldId == a.Id || SitsOn(a, b, size) || SitsOn(b, a, size);

    /// <summary>A hold that may be stacked automatically, as the upper hold or as a base: on the photo and found on it.</summary>
    /// <param name="hold">The hold.</param>
    /// <returns>False for a virtual hold and for one the last update did not find.</returns>
    public static bool CanStackAutomatically(Hold hold) => hold is { IsVirtual: false, KeptUnconfirmedGeneration: null };

    /// <summary>A hold whose outline says where it really is, so another hold can lie on it.</summary>
    /// <param name="hold">The hold.</param>
    /// <returns>False for a plain circle, whoever placed it.</returns>
    public static bool MayCarry(Hold hold) => hold.ShapePoints is { Count: >= 3 };

    /// <summary>
    /// Which holds of one photo to stack on which: every hold without a base that sits on another one goes on the
    /// SMALLEST hold it sits on. With <paramref name="fresh"/>, only pairs with a fresh hold in them are looked at, so
    /// what a person took apart stays apart. A pair that would close a loop is left out.
    /// </summary>
    /// <param name="photoHolds">Every hold of the photo.</param>
    /// <param name="fresh">The holds that were just created; null looks at all of them.</param>
    /// <param name="size">The photo's pixel size; null treats it as square.</param>
    /// <returns>The pairs, upper hold and base; nothing is written.</returns>
    public static List<(Hold Upper, Hold Base)> Plan(IReadOnlyList<Hold> photoHolds, IReadOnlySet<Guid>? fresh = null, (int Width, int Height)? size = null)
    {
        ArgumentNullException.ThrowIfNull(photoHolds);
        var known = photoHolds.Select(h => h.Id).ToHashSet();
        var on = photoHolds.ToDictionary(h => h.Id, h => h.StackedOnHoldId is { } id && known.Contains(id) ? (Guid?)id : null);
        var able = photoHolds.Where(CanStackAutomatically).Select(h => (Hold: h, Area: Area(h, size))).OrderBy(c => c.Area.Area).ThenBy(c => c.Hold.Id).ToList();
        var plan = new List<(Hold Upper, Hold Base)>();
        for (var i = 0; i < able.Count; i++)
        {
            var upper = able[i];
            if (on[upper.Hold.Id] is not null)
            {
                continue;
            }

            // Sorted by area, so the first base that fits is the smallest one.
            for (var j = i + 1; j < able.Count; j++)
            {
                var lower = able[j];
                var asked = fresh is null || fresh.Contains(upper.Hold.Id) || fresh.Contains(lower.Hold.Id);
                if (asked && MayCarry(lower.Hold) && SitsOn(upper.Area, lower.Area) && !HoldStackOrder.WouldLoop(on, upper.Hold.Id, lower.Hold.Id))
                {
                    on[upper.Hold.Id] = lower.Hold.Id;
                    plan.Add((upper.Hold, lower.Hold));
                    break;
                }
            }
        }

        return plan;
    }

    /// <summary>Whether a hold's area is clearly larger than another's: what could be its base, not a second record of it.</summary>
    /// <param name="larger">The area asked about.</param>
    /// <param name="than">The area it is compared with.</param>
    /// <returns>True from <see cref="MinAreaRatio"/> on, for an area another hold can lie on.</returns>
    internal static bool ClearlyLarger(HoldArea larger, HoldArea than) => larger.MayCarry && than.Area > 0 && larger.Area >= MinAreaRatio * than.Area;

    internal static bool SitsOn(HoldArea upper, HoldArea lower) =>
        ClearlyLarger(lower, upper) && upper.MayTouch(lower) && upper.CoveredBy([lower]) >= MinInside;

    internal static HoldArea Area(Hold hold, (int Width, int Height)? size) =>
        new(hold.X, hold.Y, hold.Radius, hold.ShapePoints, size) { MayCarry = MayCarry(hold) };
}
