// <copyright file="RedetectSafety.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Detection.Outlines;
using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Redetect;

/// <summary>What the checks say about one proposed outline.</summary>
/// <param name="Clean">True when nothing speaks against accepting it together with others.</param>
/// <param name="Unsure">True when it is likely worse than what is stored: it is shown, and kept as it is unless a person accepts it.</param>
public readonly record struct RedetectShapeCheck(bool Clean, bool Unsure);

/// <summary>
/// The checks a proposed outline has to pass before it may be accepted together with others, on top of "both outline
/// methods agree" and "about the same place and size". One press changes hundreds of holds, so each of these is a
/// reason to look at the hold instead:
/// <list type="bullet">
/// <item>the outline takes in the centre of another stored hold, or overlaps another stored outline;</item>
/// <item>it leaves the detector's box;</item>
/// <item>it has a hook, a tail or another thin part (<see cref="ThinShare"/>);</item>
/// <item>the hold has a traced outline and the new one drops or adds more than a little of it, or fills a notch of it;</item>
/// <item>the hold is a plain circle and the new outline reaches clearly beyond that circle;</item>
/// <item>boulders use the hold and its traced outline would change more than a little.</item>
/// </list>
/// Pure; the stored outlines are turned into polygons once.
/// </summary>
internal sealed class RedetectSafety
{
    /// <summary>The share of the smaller of two outlines that may lie inside the other stored hold's outline.</summary>
    public const double MaxOtherOverlap = 0.1;

    /// <summary>How far the outline may reach beyond the detector's box, as a share of the box's width or height.</summary>
    public const double BoxMargin = 0.15;

    /// <summary>From this share of the boundary on a thin part the outline has a hook or a tail.</summary>
    public const double MaxThinShare = 0.08;

    /// <summary>A part is thin when it is shallower than this share of the outline's size (the root of its area).</summary>
    public const double ThinDepth = 0.25;

    /// <summary>The share of the stored outline's area the new one may drop...</summary>
    public const double MaxLost = 0.1;

    /// <summary>...and may add, both as a share of the stored outline's area.</summary>
    public const double MaxGain = 0.15;

    /// <summary>From this share of the stored outline dropped, the new one is likely an inset or a part of the hold.</summary>
    public const double UnsureLost = 0.2;

    /// <summary>
    /// How much more of its convex hull the new outline may fill than the stored one: beyond it, a notch of the stored
    /// outline was bridged and bare wall taken in.
    /// </summary>
    public const double MaxNotchFill = 0.05;

    /// <summary>The share of an outline for a plain circle that has to lie inside that circle.</summary>
    public const double MinInsideCircle = 0.8;

    /// <summary>From this much added, as a share of the stored outline, the new one likely took in a neighbour, a shadow or bare wall.</summary>
    public const double UnsureGain = 0.6;

    /// <summary>The overlap (IoU) with the stored outline a hold that boulders use needs.</summary>
    public const double BoulderIou = 0.85;

    private const int Samples = 96;

    private readonly List<(Hold Hold, P2[] Outline, double MinX, double MinY, double MaxX, double MaxY)> stored;
    private readonly double aspect;

    /// <summary>Initializes a new instance of the <see cref="RedetectSafety"/> class.</summary>
    /// <param name="input">The planner's input.</param>
    /// <param name="aspect">Photo width / height.</param>
    public RedetectSafety(RedetectInput input, double aspect)
    {
        this.aspect = aspect > 0 ? aspect : 1;
        stored = [.. input.Existing.Select(e => e.Hold).Where(RedetectRules.IsMatchable).Select(h =>
        {
            var outline = HoldShapePolygon.Of(h);
            return (h, outline, outline.Min(p => p.X), outline.Min(p => p.Y), outline.Max(p => p.X), outline.Max(p => p.Y));
        })];
    }

    /// <summary>
    /// The share of an outline's boundary that lies on a thin part: stepping <see cref="ThinDepth"/> of the outline's
    /// size inwards from there leaves the outline again. A compact hold gives 0 to a few percent (its corners), a hook,
    /// a tail along an edge or a sliver gives much more.
    /// </summary>
    /// <param name="outline">The outline in photo fractions.</param>
    /// <param name="aspect">Photo width / height.</param>
    /// <returns>The share, 0..1.</returns>
    public static double ThinShare(IReadOnlyList<P2> outline, double aspect)
    {
        var points = outline.Select(p => new P2(p.X * aspect, p.Y)).ToList();
        double signed = ShapeGeometry.SignedArea(points), total = ShapeGeometry.Perimeter(points);
        if (points.Count < 3 || Math.Abs(signed) <= 0 || total <= 0)
        {
            return 0;
        }

        double depth = ThinDepth * Math.Sqrt(Math.Abs(signed)), side = signed > 0 ? 1 : -1;
        int thin = 0, edge = 0;
        double before = 0;
        for (int k = 0; k < Samples; k++)
        {
            double at = (k + 0.5) * total / Samples;
            double length = (points[(edge + 1) % points.Count] - points[edge]).Length;
            while (at - before > length && edge < points.Count - 1)
            {
                before += length;
                edge++;
                length = (points[(edge + 1) % points.Count] - points[edge]).Length;
            }

            var (from, to) = (points[edge], points[(edge + 1) % points.Count]);
            var along = (to - from) * (1 / Math.Max(1e-12, length));
            var on = from + (along * (at - before));
            var inwards = new P2(-along.Y * side, along.X * side);
            thin += ShapeGeometry.Contains(points, on + (inwards * depth)) ? 0 : 1;
        }

        return (double)thin / Samples;
    }

    /// <summary>Checks the outline a detection proposes for a stored hold.</summary>
    /// <param name="existing">The stored hold.</param>
    /// <param name="detection">The detection.</param>
    /// <returns>Whether it is clean, and whether it is likely worse than what is stored.</returns>
    public RedetectShapeCheck Check(RedetectExisting existing, Hold detection)
    {
        var hold = existing.Hold;
        var outline = HoldShapePolygon.Of(detection);
        bool traced = hold.ShapePoints is { Count: >= 3 };
        var (oldArea, newArea, shared) = ShapeOverlapArea.Measure(HoldShapePolygon.Of(hold), outline);
        double lost = traced && oldArea > 0 ? (oldArea - shared) / oldArea : 0;
        double gain = traced && oldArea > 0 ? (newArea - shared) / oldArea : 0;
        double iou = shared / Math.Max(1e-12, oldArea + newArea - shared);
        var (centre, overlap) = Neighbours(hold, outline);
        bool thin = ThinShare(outline, aspect) > MaxThinShare;
        bool fits = traced
            ? lost <= MaxLost && gain <= MaxGain && Solidity(outline) - Solidity(HoldShapePolygon.Of(hold)) <= MaxNotchFill
            : shared >= MinInsideCircle * newArea;
        bool clean = !centre && !overlap && !thin && InsideBox(detection) && fits
            && (existing.Boulders.Count == 0 || !traced || iou >= BoulderIou);
        return new RedetectShapeCheck(clean, centre || thin || lost >= UnsureLost || gain >= UnsureGain);
    }

    /// <summary>The share of its convex hull an outline fills: 1 for a convex one, less with every notch.</summary>
    private static double Solidity(IReadOnlyList<P2> outline)
    {
        double hull = ShapeGeometry.Area(ShapeGeometry.ConvexHull(outline));
        return hull > 0 ? ShapeGeometry.Area(outline) / hull : 1;
    }

    /// <summary>True when the outline stays inside the detector's box plus <see cref="BoxMargin"/>; true without a box.</summary>
    private static bool InsideBox(Hold detection)
    {
        if (detection.DetectedBox is not { } box || detection.ShapePoints is not { Count: >= 3 } points)
        {
            return true;
        }

        double halfWidth = box.Width * (0.5 + BoxMargin), halfHeight = box.Height * (0.5 + BoxMargin);
        return points.All(p => Math.Abs(p.Dx) <= halfWidth && Math.Abs(p.Dy) <= halfHeight);
    }

    /// <summary>Whether the outline takes in another stored hold's centre, and whether it overlaps another stored outline.</summary>
    private (bool Centre, bool Overlap) Neighbours(Hold hold, P2[] outline)
    {
        double minX = outline.Min(p => p.X), minY = outline.Min(p => p.Y), maxX = outline.Max(p => p.X), maxY = outline.Max(p => p.Y);
        bool centre = false, overlap = false;
        foreach (var other in stored)
        {
            // A hold on this one, and the hold this one sits on, lie on it by design.
            if (other.Hold.Id == hold.Id || other.Hold.StackedOnHoldId == hold.Id || hold.StackedOnHoldId == other.Hold.Id
                || other.MaxX < minX || other.MinX > maxX || other.MaxY < minY || other.MinY > maxY)
            {
                continue;
            }

            centre |= ShapeGeometry.Contains(outline, new P2(other.Hold.X, other.Hold.Y));
            var (area, otherArea, shared) = ShapeOverlapArea.Measure(outline, other.Outline);
            overlap |= shared > MaxOtherOverlap * Math.Min(area, otherArea);
        }

        return (centre, overlap);
    }
}
