// <copyright file="HoldMoveTolerance.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry.View3D;

namespace Blocwerk.Core.HoldMoves;

/// <summary>
/// How far a carried hold may appear to shift between two photos without having moved. Both move measures map the detected
/// centre flat onto the wall, so the tolerance is what moves that centre on an unmoved hold:
/// <list type="bullet">
/// <item>how far a re-detected centre wanders, the larger of two estimates: the measure's own noise (the 3D neighbourhood's
/// spread, or the panel's typical photo shift: its 90th percentile over all carried pairs, most of which did not move; never
/// below <see cref="HoldMoveOptions.MinNoiseMm"/>), and a share of the hold's size (<see cref="HoldMoveOptions.SizeShare"/>:
/// a big hold's outline takes in a shadow or a bit of its volume, the panel's typical hold does not say that);</item>
/// <item>plus depth: a hold standing out of the wall by its apex can appear shifted by up to that much between two
/// viewpoints. The panel photos' poses cannot be recovered reliably (a cropped photo has no known principal point, and a photo
/// of one main facet makes the resection degenerate), so the tolerance is widened by the apex instead of correcting for it.</item>
/// </list>
/// Pure.
/// </summary>
public static class HoldMoveTolerance
{
    /// <summary>A panel's photo noise is only measured over at least this many carried pairs.</summary>
    public const int MinNoiseSamples = 20;

    /// <summary>The tolerance of a 3D (differential) measure.</summary>
    /// <param name="spreadMm">The neighbourhood's residual spread, mm.</param>
    /// <param name="old">The old hold.</param>
    /// <param name="twin">Its successor.</param>
    /// <param name="options">The thresholds.</param>
    /// <returns>The tolerance, mm.</returns>
    public static double ThreeD(double spreadMm, Hold old, Hold twin, HoldMoveOptions options) =>
        Combine(Math.Max(options.MinNoiseMm, options.SpreadFactor * spreadMm), old, twin, options);

    /// <summary>The tolerance of a photo estimate.</summary>
    /// <param name="panelNoiseMm">The panel's typical photo shift (<see cref="PanelNoiseMm"/>), or null when unknown.</param>
    /// <param name="old">The old hold.</param>
    /// <param name="twin">Its successor.</param>
    /// <param name="options">The thresholds.</param>
    /// <returns>The tolerance, mm.</returns>
    public static double Photo(double? panelNoiseMm, Hold old, Hold twin, HoldMoveOptions options)
    {
        var floor = panelNoiseMm is { } noise ? Math.Max(options.MinNoiseMm, options.PanelNoiseFactor * noise) : options.NoiseMm2D;
        return Combine(floor, old, twin, options);
    }

    /// <summary>The 90th percentile of a panel's photo shifts, or null with fewer than <see cref="MinNoiseSamples"/>.</summary>
    /// <param name="shiftsMm">The photo estimate of every carried pair on the panel, mm.</param>
    /// <returns>The typical shift of an unmoved hold, mm.</returns>
    public static double? PanelNoiseMm(IEnumerable<double> shiftsMm)
    {
        var sorted = shiftsMm.Order().ToList();
        return sorted.Count < MinNoiseSamples ? null : sorted[(int)(0.9 * (sorted.Count - 1))];
    }

    /// <summary>The larger of the measure's noise floor and the hold's size share, plus its apex.</summary>
    /// <param name="floorMm">The measure's noise floor, mm.</param>
    /// <param name="old">The old hold.</param>
    /// <param name="twin">Its successor.</param>
    /// <param name="options">The thresholds.</param>
    /// <returns>The tolerance, mm.</returns>
    public static double Combine(double floorMm, Hold old, Hold twin, HoldMoveOptions options) =>
        Math.Max(floorMm, options.SizeShare * HalfSizeMm(old, twin)) + ApexMm(old);

    /// <summary>Half the largest metric extent of the pair, mm (0 when neither has a metric size).</summary>
    private static double HalfSizeMm(Hold old, Hold twin) =>
        new[] { old.WidthMm, old.HeightMm, twin.WidthMm, twin.HeightMm }.Max(v => v ?? 0) / 2;

    /// <summary>How far the old hold stands out of its facet: its measured apex, else the size estimate.</summary>
    private static double ApexMm(Hold old)
    {
        var protrusion = HoldProtrusion.For(old) ?? HoldProtrusion.Estimate(old.WidthMm ?? 0, old.HeightMm ?? 0, string.Empty);
        return Math.Max(0, protrusion.ApexMm);
    }
}
