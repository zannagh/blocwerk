// <copyright file="RedetectRules.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Detection.Outlines;
using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Redetect;

/// <summary>The small rules of a re-detect about one detection: is it traced, is it turned down, is a circle its placeholder. Pure.</summary>
internal static class RedetectRules
{
    /// <summary>A detection under this share of the photo's median detection size can be a part of a hold.</summary>
    public const double PartSizeShare = 0.4;

    /// <summary>...and so can one under this size, where its size in millimetres is known: no hold is smaller.</summary>
    public const double PartSizeMm = 25;

    /// <summary>
    /// A new detection on bare wall measured smaller than this is a screw hole, a bolt hole or a knot: on the Attic's
    /// two photos every one of them was, and no hold. A stored hold of that size is left alone.
    /// </summary>
    public const double NewHoldMinMm = 40;

    /// <summary>
    /// Inside another hold the limit is higher: a bolt recess with its washer or a pocket measures up to this, and a hold
    /// screwed onto a hold is larger.
    /// </summary>
    public const double InsidePartSizeMm = 40;

    /// <summary>...and where the surrounding hold was never measured, a part covers less than this share of its area.</summary>
    public const double InsidePartAreaShare = 0.15;

    /// <summary>A detection counts as inside a stored hold when this share of its outline's points lie in that hold's outline.</summary>
    public const double InsideShare = 0.8;

    /// <summary>An outline at least this many times longer than wide, on a detection smaller than normal, is a sliver.</summary>
    public const double SliverAspect = 4;

    /// <summary>
    /// True when a detection is a sliver: smaller than the photo's median detection and several times longer than it is
    /// wide. That is the gap between two holds or the edge of a shadow; a rail is thin too, but large.
    /// </summary>
    /// <param name="shape">The detection.</param>
    /// <param name="medianRadius">The median radius of this photo's detections.</param>
    /// <returns>Whether it is a sliver.</returns>
    public static bool IsSliver(Hold shape, double medianRadius)
    {
        if (shape.ShapePoints is not { Count: >= 3 } points || shape.Radius >= medianRadius)
        {
            return false;
        }

        // The thinnest and the longest extent over directions 15 degrees apart.
        double thinnest = double.MaxValue, longest = 0;
        for (int k = 0; k < 12; k++)
        {
            double angle = k * Math.PI / 12, cos = Math.Cos(angle), sin = Math.Sin(angle);
            var along = points.Select(p => (p.Dx * cos) + (p.Dy * sin)).ToList();
            double extent = along.Max() - along.Min();
            (thinnest, longest) = (Math.Min(thinnest, extent), Math.Max(longest, extent));
        }

        return longest >= SliverAspect * thinnest;
    }

    /// <summary>True when the detection has an outline that was really traced: not a plain circle and not a rough shape.</summary>
    public static bool IsTraced(Hold detection) =>
        detection.ShapePoints is { Count: >= 3 } && detection.OutlineConfidence is not <= HoldOutlineRefiner.CircleConfidenceCeiling;

    /// <summary>The earlier answer about the detection's spot, or null when there is none.</summary>
    public static RedetectEarlier? EarlierAnswer(RedetectInput input, int detection, HoldSamenessOptions options)
    {
        var shape = input.Detections[detection].Shape;
        return input.TurnedDown.Where(t => t.Spot is var spot
            && Math.Abs(spot.X - shape.X) <= spot.Radius + shape.Radius
            && Math.Abs(spot.Y - shape.Y) <= (spot.Radius + shape.Radius) * 2
            && HoldSameness.SamePlace(HoldSameness.Measure(spot, spot.ShapePoints is { Count: >= 3 } ? shape : new Hold { X = shape.X, Y = shape.Y, Radius = shape.Radius }), options))
            .Select(t => t.Earlier)
            .OrderBy(e => e.Kind)
            .FirstOrDefault();
    }

    /// <summary>True for a hold a re-detect may look at: not virtual and not hidden as kept but not confirmed.</summary>
    /// <param name="hold">The hold.</param>
    /// <returns>Whether it takes part.</returns>
    public static bool IsMatchable(Hold hold)
    {
        ArgumentNullException.ThrowIfNull(hold);
        return !hold.IsVirtual && hold.KeptUnconfirmedGeneration is null;
    }

    /// <summary>True when a person placed the hold or drew or edited its shape: that shape is never replaced.</summary>
    /// <param name="hold">The hold.</param>
    /// <returns>Whether its shape is hand-made.</returns>
    public static bool IsHandShaped(Hold hold)
    {
        ArgumentNullException.ThrowIfNull(hold);
        return HoldMergeRules.IsHandMade(hold) || hold.ShapeControlPoints is { Count: > 0 };
    }

    /// <summary>
    /// True when a stored plain circle stands for the traced detection around it: the circle only marks the spot, so a
    /// detection that is clearly larger or smaller than it is still that hold while the circle's centre lies in it.
    /// </summary>
    public static bool IsPlaceholderOf(Hold circle, Hold detection, HoldOverlap overlap, HoldSamenessOptions options) =>
        detection.ShapePoints is { Count: >= 3 }
        && overlap.SizeRatio <= options.MaxPlaceholderSizeRatio
        && ShapeGeometry.Contains(HoldShapePolygon.Of(detection), new P2(circle.X, circle.Y));

    /// <summary>
    /// True when a detection is a part of a hold and not a hold: it is tiny for this photo (under
    /// <see cref="PartSizeShare"/> of the median detection) and lies inside the traced outline of another detection
    /// or of a stored hold. That is a bolt hole, a screw head or a pocket: they are inside a hold, never a hold.
    /// </summary>
    /// <param name="input">The planner's input.</param>
    /// <param name="detection">Index of the detection.</param>
    /// <param name="medianRadius">The median radius of this photo's detections.</param>
    /// <returns>Whether it is a part.</returns>
    public static bool IsPartOfAHold(RedetectInput input, int detection, double medianRadius)
    {
        return IsPart(input.Detections[detection].Shape, input, medianRadius);
    }

    /// <summary>The same for any hold of the photo, stored or detected: tiny, and inside another one's traced outline.</summary>
    /// <param name="shape">The hold.</param>
    /// <param name="input">The planner's input.</param>
    /// <param name="medianRadius">The median radius of this photo's detections.</param>
    /// <returns>Whether it is a part.</returns>
    public static bool IsPart(Hold shape, RedetectInput input, double medianRadius)
    {
        // In millimetres where the hold was measured, else against what is normal on this photo.
        bool tiny = shape is { WidthMm: > 0, HeightMm: > 0 }
            ? Math.Max(shape.WidthMm.Value, shape.HeightMm.Value) < InsidePartSizeMm
            : shape.Radius < PartSizeShare * medianRadius;
        if (!tiny)
        {
            return false;
        }

        // Inside the traced outline of another detection, or inside a stored hold (its outline, or its circle).
        var centre = new P2(shape.X, shape.Y);
        bool Near(Hold other) => !ReferenceEquals(other, shape) && other.Radius > shape.Radius
            && Math.Abs(other.X - shape.X) <= other.Radius * 2 && Math.Abs(other.Y - shape.Y) <= other.Radius * 3;
        return input.Detections.Any(d => Near(d.Shape) && d.Shape.ShapePoints is { Count: >= 3 } && ShapeGeometry.Contains(HoldShapePolygon.Of(d.Shape), centre))
            || input.Existing.Any(e => IsMatchable(e.Hold) && Near(e.Hold) && ShapeGeometry.Contains(HoldShapePolygon.Of(e.Hold), centre));
    }

    /// <summary>A base is at least this many times the area of each detection on it.</summary>
    public const double BaseAreaFactor = 3;

    /// <summary>
    /// The detections lying on a detection that is a base: a large coloured hold with smaller holds screwed onto it.
    /// Each of them has its centre inside the base's outline and is at most a third of its area. A board or volume
    /// (bare wood or white, as read off the photo) is no base: what lies on it are pockets or holds on the wall.
    /// </summary>
    /// <param name="input">The planner's input.</param>
    /// <param name="detection">Index of the detection.</param>
    /// <returns>The indexes of the detections on it; empty when it is no base.</returns>
    public static List<int> OnBase(RedetectInput input, int detection)
    {
        var baseHold = input.Detections[detection];
        // A base is a detection the detector is sure enough of, cast in a colour. The colour reader declines a weak
        // red or a colour between two (salmon); such a base needs several holds on it instead.
        if (baseHold.Shape.ShapePoints is not { Count: >= 3 } || baseHold.Shape.Confidence < MinBaseConfidence
            || (baseHold.ColorRead && !IsColoured(baseHold.Color)))
        {
            return [];
        }

        var outline = HoldShapePolygon.Of(baseHold.Shape);
        double area = ShapeGeometry.Area(outline);
        List<int> on = [.. Enumerable.Range(0, input.Detections.Count).Where(i => i != detection
            && input.Detections[i].Shape is var other
            && ShapeGeometry.Contains(outline, new P2(other.X, other.Y))
            && ShapeGeometry.Area(HoldShapePolygon.Of(other)) * BaseAreaFactor <= area)];

        // A base is a hold of its own colour: when every hold on it has that colour, it is more likely one hold seen in parts.
        if (!baseHold.ColorRead)
        {
            return on.Count >= MinHoldsOnUnreadBase ? on : [];
        }

        return on.Any(i => !string.Equals(input.Detections[i].Color, baseHold.Color, StringComparison.OrdinalIgnoreCase)) ? on : [];
    }

    /// <summary>
    /// Where the colour could not be read off the photo, a base needs this many holds on it: a wooden volume with one
    /// hold on it is common, a large hold with several is not.
    /// </summary>
    public const int MinHoldsOnUnreadBase = 2;

    /// <summary>The detector confidence a base needs.</summary>
    public const double MinBaseConfidence = 0.5;

    /// <summary>Two new detections are the same hold from this overlap (IoU) of their outlines on.</summary>
    public const double DuplicateIou = 0.5;

    /// <summary>
    /// The new detections without the second detection of one hold: of two whose outlines overlap by
    /// <see cref="DuplicateIou"/> or more, the one the detector is surer of stays.
    /// </summary>
    /// <param name="input">The planner's input.</param>
    /// <param name="detections">Indexes of the new detections.</param>
    /// <returns>The indexes that stay, in order.</returns>
    public static List<int> WithoutDuplicates(RedetectInput input, List<int> detections)
    {
        var order = detections.OrderByDescending(i => input.Detections[i].Shape.Confidence).ThenBy(i => i).ToList();
        var kept = new List<int>();
        foreach (int i in order)
        {
            var shape = input.Detections[i].Shape;
            if (!kept.Any(k => input.Detections[k].Shape is var other
                && Math.Abs(other.X - shape.X) <= other.Radius + shape.Radius && Math.Abs(other.Y - shape.Y) <= 2 * (other.Radius + shape.Radius)
                && HoldSameness.Measure(other, shape).Iou >= DuplicateIou))
            {
                kept.Add(i);
            }
        }

        return [.. kept.Order()];
    }

    /// <summary>True when the detection lies on another detection that is a base (<see cref="OnBase"/>).</summary>
    /// <param name="input">The planner's input.</param>
    /// <param name="detection">Index of the detection.</param>
    /// <returns>Whether it is a hold on a base.</returns>
    public static bool IsOnABase(RedetectInput input, int detection)
    {
        var shape = input.Detections[detection].Shape;
        return Enumerable.Range(0, input.Detections.Count).Any(b => b != detection
            && input.Detections[b].Shape is { ShapePoints.Count: >= 3 } other && other.Radius > shape.Radius
            && ShapeGeometry.Contains(HoldShapePolygon.Of(other), new P2(shape.X, shape.Y))
            && OnBase(input, b).Contains(detection));
    }

    private static bool IsDark(string? colour) => colour is "black" or "gray" or "grey";

    /// <summary>True for a colour a hold is cast in; bare wood, white (volumes, boards) and unknown are not.</summary>
    private static bool IsColoured(string? colour) =>
        colour is { Length: > 0 } && !colour.StartsWith("wood", StringComparison.OrdinalIgnoreCase)
        && !colour.Equals("white", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True for a detection on an empty spot that is a part of a hold, a screw or bolt hole (<see cref="NewHoldMinMm"/>),
    /// a sliver, a printed marker, or a board or volume that covers several holds.
    /// </summary>
    /// <param name="input">The planner's input.</param>
    /// <param name="detection">Index of the detection.</param>
    /// <param name="medianRadius">The median radius of this photo's detections.</param>
    /// <returns>Whether it is no hold.</returns>
    public static bool IsNoHold(RedetectInput input, int detection, double medianRadius)
    {
        var shape = input.Detections[detection].Shape;
        bool isBase = OnBase(input, detection).Count > 0;

        // A small coloured hold screwed onto a large hold is a hold: neither a part of the base nor a screw hole.
        // A dark spot on a base (a bolt hole) is not exempt.
        if (IsOnABase(input, detection) && IsColoured(input.Detections[detection].Color) && !IsDark(input.Detections[detection].Color))
        {
            return IsSliver(shape, medianRadius) || RedetectMarkers.On(input.Markers, shape);
        }

        return IsPartOfAHold(input, detection, medianRadius) || IsInsideAHold(shape, input) || IsTooSmall(shape) || IsSliver(shape, medianRadius)
            || shape is { WidthMm: > 0, HeightMm: > 0 } && Math.Max(shape.WidthMm.Value, shape.HeightMm.Value) < NewHoldMinMm
            || RedetectMarkers.On(input.Markers, shape)
            || (!isBase && shape.ShapePoints is { Count: >= 3 } && RedetectSafety.CoveredHolds(input, HoldShapePolygon.Of(shape), null) >= RedetectSafety.MaxCoveredHolds);
    }

    /// <summary>True when a detection was measured and is smaller than any hold (<see cref="PartSizeMm"/>): a bolt hole, a screw head, a knot.</summary>
    /// <param name="shape">The detection.</param>
    /// <returns>Whether it is too small to be a hold.</returns>
    public static bool IsTooSmall(Hold shape) =>
        shape is { WidthMm: > 0, HeightMm: > 0 } && Math.Max(shape.WidthMm.Value, shape.HeightMm.Value) < PartSizeMm;

    /// <summary>
    /// True when a traced detection lies inside the outline of a stored hold and is small against it: a bolt recess, a
    /// washer or a pocket of that hold. Its size comes from the surrounding hold's measured size
    /// (<see cref="InsidePartSizeMm"/>), or from its share of that hold's area (<see cref="InsidePartAreaShare"/>).
    /// </summary>
    /// <param name="detection">The detection.</param>
    /// <param name="input">The planner's input.</param>
    /// <returns>Whether it is a part of a stored hold.</returns>
    public static bool IsInsideAHold(Hold detection, RedetectInput input)
    {
        if (detection.ShapePoints is not { Count: >= 3 } points)
        {
            return false;
        }

        double area = ShapeGeometry.Area(HoldShapePolygon.Of(detection));
        foreach (var hold in input.Existing.Select(e => e.Hold).Where(h => IsMatchable(h) && h.ShapePoints is { Count: >= 3 }))
        {
            var outline = HoldShapePolygon.Of(hold);
            if (!ShapeGeometry.Contains(outline, new P2(detection.X, detection.Y))
                || points.Count(p => ShapeGeometry.Contains(outline, new P2(detection.X + p.Dx, detection.Y + p.Dy))) < InsideShare * points.Count)
            {
                continue;
            }

            double share = area / Math.Max(1e-12, ShapeGeometry.Area(outline));
            bool small = hold is { WidthMm: > 0, HeightMm: > 0 }
                ? Math.Sqrt(share * hold.WidthMm.Value * hold.HeightMm.Value) < InsidePartSizeMm
                : share < InsidePartAreaShare;
            if (small)
            {
                return true;
            }
        }

        return false;
    }
}
