// <copyright file="ReOutlineRule.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.LookOver;
using Blocwerk.Core.Services;

namespace Blocwerk.Core.HoldMoves;

/// <summary>
/// "Did the hold move, or did only its outline change?" A carried pair whose centres are apart can be the same hold drawn
/// differently: a hand-placed circle replaced by the detected outline, or an outline that grew over a part of the hold the
/// old one missed. The centre then shifts while nothing moved (The Attic, gen 5 → 6: most of 40 "moved" holds). Only such a
/// pair is a re-outline, and only when everything agrees, because a wrong verdict hides a real move from its boulders:
/// <list type="bullet">
/// <item>the outline really changed: a circle became a traced outline, or the area changed by <see cref="OutlineChangeRatio"/>
/// or more (an unchanged hold that shifted is measured as usual, a 2:1 rail turned in place too);</item>
/// <item>the hold did not turn by <see cref="HoldMoveOptions.MinRotationDeg"/> or more;</item>
/// <item>one centre lies inside the other footprint and the larger covers at least <see cref="MinCovered"/> of the smaller;</item>
/// <item>the shift is at most <see cref="MaxShiftFraction"/> of the larger footprint's radius, and what the change of size
/// cannot explain (the shift beyond the difference of the radii) is under the photo noise floor
/// (<see cref="HoldMoveOptions.NoiseMm2D"/>).</item>
/// </list>
/// Both footprints are on the new photo (the old one where the matcher's warp puts it). Pure.
/// </summary>
public static class ReOutlineRule
{
    /// <summary>The area ratio from which an outline counts as changed.</summary>
    public const double OutlineChangeRatio = 1.5;

    /// <summary>The share of the smaller footprint the larger must cover.</summary>
    public const double MinCovered = 0.5;

    /// <summary>The largest shift, as a fraction of the larger footprint's radius.</summary>
    public const double MaxShiftFraction = 0.75;

    /// <summary>Whether the pair's displacement is explained by the change of outline.</summary>
    /// <param name="old">The old hold.</param>
    /// <param name="twin">Its successor on the new photo.</param>
    /// <param name="move">Where the warp puts the old hold, its warped outline (or null), the new photo's size and the old → new scale.</param>
    /// <param name="measuredMm">The measured displacement, mm (3D or photo).</param>
    /// <param name="rotationDeg">How far the hold turned, when known.</param>
    /// <param name="options">The thresholds.</param>
    /// <returns>True when it is a re-outline, not a move.</returns>
    public static bool Explains(Hold old, Hold twin, WarpedFootprint? move, double measuredMm, double? rotationDeg, HoldMoveOptions options)
    {
        // An outlined old hold is compared by its warped outline; without one there is nothing to compare.
        if (move is not { } w || (old.ShapePoints is { Count: >= 3 } && w.Shape is not { Count: >= 3 }))
        {
            return false;
        }

        var shape = w.Shape is { Count: >= 3 } s ? s.Select(v => new ShapePoint { Dx = v.X - w.At.X, Dy = v.Y - w.At.Y }).ToList() : null;
        var before = new HoldArea(w.At.X, w.At.Y, old.Radius * w.Scale, shape, w.Size);
        var after = new HoldArea(twin.X, twin.Y, twin.Radius, twin.ShapePoints, w.Size);
        return Explains(before, old.ShapePoints is { Count: >= 3 }, after, twin.ShapePoints is { Count: >= 3 }, measuredMm, rotationDeg, options);
    }

    /// <summary>The rule on two footprints of one photo (see <see cref="ReOutlineRule"/>).</summary>
    /// <param name="before">The old footprint.</param>
    /// <param name="beforeOutlined">Whether the old hold had a traced or drawn outline (else a circle).</param>
    /// <param name="after">The new footprint.</param>
    /// <param name="afterOutlined">Whether the new hold has one.</param>
    /// <param name="measuredMm">The measured displacement, mm.</param>
    /// <param name="rotationDeg">How far the hold turned, when known.</param>
    /// <param name="options">The thresholds.</param>
    /// <returns>True when it is a re-outline.</returns>
    internal static bool Explains(
        HoldArea before, bool beforeOutlined, HoldArea after, bool afterOutlined, double measuredMm, double? rotationDeg, HoldMoveOptions options)
    {
        var (small, large) = before.Area <= after.Area ? (before, after) : (after, before);
        if (rotationDeg >= options.MinRotationDeg || small.Area <= 0 || !small.MayTouch(large))
        {
            return false;
        }

        var changed = (!beforeOutlined && afterOutlined) || large.Area >= OutlineChangeRatio * small.Area;
        var centreInside = after.Contains(before.CentreX, before.CentreY) || before.Contains(after.CentreX, after.CentreY);
        if (!changed || !centreInside || small.CoveredBy([large]) < MinCovered)
        {
            return false;
        }

        var (bigR, smallR) = (Math.Sqrt(large.Area / Math.PI), Math.Sqrt(small.Area / Math.PI));
        var shift = Math.Sqrt(Math.Pow(after.CentreX - before.CentreX, 2) + Math.Pow(after.CentreY - before.CentreY, 2));
        var unexplainedMm = shift <= 0 ? 0 : Math.Max(0, shift - (bigR - smallR)) / shift * measuredMm;
        return shift <= MaxShiftFraction * bigR && unexplainedMm < options.NoiseMm2D;
    }
}

/// <summary>Where the matcher's warp puts an old hold on the new photo, as the re-outline rule compares it.</summary>
/// <param name="At">Its warped centre (normalized).</param>
/// <param name="Shape">Its warped outline (absolute, normalized), or null for a circle.</param>
/// <param name="Size">The new photo's size, or null (normalized units).</param>
/// <param name="Scale">New-photo units per old-photo unit, for an old circle's radius.</param>
public readonly record struct WarpedFootprint(HoldPositionNorm At, IReadOnlyList<HoldPositionNorm>? Shape, (int Width, int Height)? Size, double Scale = 1);
