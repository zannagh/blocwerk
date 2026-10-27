// <copyright file="SurfacePreview.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Geometry.Surface;

namespace Blocwerk.Core.Services;

/// <summary>What "place holds on the 3D surface" would do (nothing is written).</summary>
/// <param name="Checked">Holds of registered panel photos.</param>
/// <param name="OwnView">Placed from their own photo.</param>
/// <param name="SharedView">Placed at a linked copy's position (seen more head-on in the other photo).</param>
/// <param name="Fallback">Missed by the surface, placed by the outlier check.</param>
/// <param name="Missed">Left as they are.</param>
/// <param name="Pairs">Linked holds both photos placed.</param>
/// <param name="PairGapMedianMm">Median 3D distance between those two independent placements.</param>
/// <param name="PairGapP90Mm">Their 90th percentile.</param>
/// <param name="Panels">Per panel photo.</param>
public sealed record SurfacePreview(
    int Checked, int OwnView, int SharedView, int Fallback, int Missed, int Pairs, double? PairGapMedianMm, double? PairGapP90Mm,
    IReadOnlyList<SurfacePanelCount> Panels)
{
    /// <summary>Gets the holds it would write.</summary>
    public int ToPlace => OwnView + SharedView + Fallback;

    /// <summary>The preview of a plan.</summary>
    public static SurfacePreview Of(SurfacePlan plan)
    {
        var gaps = plan.PairGapsMm.Order().ToList();
        double? At(double q) => gaps.Count == 0 ? null : Math.Round(gaps[(int)Math.Floor(q * (gaps.Count - 1))], 1);
        return new(
            plan.Panels.Sum(p => p.Checked),
            plan.Panels.Sum(p => p.OwnView),
            plan.Panels.Sum(p => p.SharedView),
            plan.Panels.Sum(p => p.Fallback),
            plan.Panels.Sum(p => p.Missed),
            gaps.Count,
            At(0.5),
            At(0.9),
            plan.Panels);
    }
}
