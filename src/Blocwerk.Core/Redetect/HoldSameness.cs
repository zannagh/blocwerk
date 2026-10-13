// <copyright file="HoldSameness.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Detection.Outlines;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Holds.Colors;

namespace Blocwerk.Core.Redetect;

/// <summary>
/// The one rule for "these two outlines are the same physical hold". Pure. Two holds are the same when they are in
/// the same place (their outlines overlap enough, or their centres nearly coincide), are of a similar size, and their
/// colours do not clearly disagree. A much smaller hold inside a larger one is stacked on it and never the same.
/// </summary>
public static class HoldSameness
{
    /// <summary>Measures how the outlines of two holds on one photo lie to each other.</summary>
    /// <param name="a">One hold.</param>
    /// <param name="b">The other.</param>
    /// <returns>The overlap; no overlap and an infinite offset when one of them has no area.</returns>
    public static HoldOverlap Measure(Hold a, Hold b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        var (areaA, areaB, shared) = ShapeOverlapArea.Measure(HoldShapePolygon.Of(a), HoldShapePolygon.Of(b));
        var smaller = Math.Min(areaA, areaB);
        if (smaller <= 0)
        {
            return new HoldOverlap(0, 0, double.PositiveInfinity, double.PositiveInfinity);
        }

        var distance = Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));
        return new HoldOverlap(
            shared / (areaA + areaB - shared),
            shared / smaller,
            Math.Sqrt(Math.Max(areaA, areaB) / smaller),
            distance / Math.Sqrt(smaller / Math.PI));
    }

    /// <summary>True when the two outlines are in the same place and of a similar size, whatever their colours.</summary>
    /// <param name="overlap">The measured overlap.</param>
    /// <param name="options">The thresholds.</param>
    /// <returns>Whether they are in the same place.</returns>
    public static bool SamePlace(HoldOverlap overlap, HoldSamenessOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return overlap.SizeRatio <= options.MaxSizeRatio
            && (overlap.Score >= options.MinScore || overlap.CentreOffset <= options.MaxCentreOffset);
    }

    /// <summary>The verdict for two outlines and their colours.</summary>
    /// <param name="overlap">The measured overlap.</param>
    /// <param name="color">How the two colours relate.</param>
    /// <param name="options">The thresholds.</param>
    /// <returns>The verdict.</returns>
    public static HoldSamenessVerdict Judge(HoldOverlap overlap, HoldColorRelation color, HoldSamenessOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (overlap.SizeRatio > options.MaxSizeRatio)
        {
            return overlap.Containment >= options.StackedContainment ? HoldSamenessVerdict.Stacked : HoldSamenessVerdict.Different;
        }

        if (!SamePlace(overlap, options))
        {
            return HoldSamenessVerdict.Different;
        }

        if (color == HoldColorRelation.Different)
        {
            return HoldSamenessVerdict.ColorClash;
        }

        var sure = color == HoldColorRelation.Same ? options.SureScore : options.SureScoreUnknownColor;
        return overlap.Score >= sure ? HoldSamenessVerdict.Same : HoldSamenessVerdict.Likely;
    }
}
