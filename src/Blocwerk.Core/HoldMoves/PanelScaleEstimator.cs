// <copyright file="PanelScaleEstimator.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;

namespace Blocwerk.Core.HoldMoves;

/// <summary>
/// Millimetres per pixel of a new panel photo, taken from the holds that were re-found: their old wall positions
/// (millimetres on a facet) against where they now are in the new photo. The median over pairs of holds is used, so
/// the few holds that really moved do not skew it. Pure and deterministic.
/// </summary>
public static class PanelScaleEstimator
{
    private const int MaxAnchors = 80;
    private const double MinPixelGap = 50;
    private const double MinMmGap = 100;
    private const int MinRatios = 3;

    /// <summary>The scale, or null when too few placed pairs are known.</summary>
    /// <param name="pairs">Old holds with their successors on this panel's new photo.</param>
    /// <param name="size">The new photo's size, px.</param>
    /// <returns>Millimetres per pixel.</returns>
    public static double? Estimate(IEnumerable<(Hold Old, Hold Twin)> pairs, (int Width, int Height) size)
    {
        var anchors = pairs
            .Where(p => p.Old is { FacetId: not null, PlaneAMm: not null, PlaneBMm: not null })
            .OrderBy(p => p.Old.Id)
            .Take(MaxAnchors)
            .ToList();
        var ratios = new List<double>();
        for (var i = 0; i < anchors.Count; i++)
        {
            for (var j = i + 1; j < anchors.Count; j++)
            {
                if (Ratio(anchors[i], anchors[j], size) is { } r)
                {
                    ratios.Add(r);
                }
            }
        }

        if (ratios.Count < MinRatios)
        {
            return null;
        }

        ratios.Sort();
        return ratios[ratios.Count / 2];
    }

    private static double? Ratio((Hold Old, Hold Twin) a, (Hold Old, Hold Twin) b, (int Width, int Height) size)
    {
        if (a.Old.FacetId != b.Old.FacetId)
        {
            return null;
        }

        var mm = Math.Sqrt(Math.Pow(a.Old.PlaneAMm!.Value - b.Old.PlaneAMm!.Value, 2) + Math.Pow(a.Old.PlaneBMm!.Value - b.Old.PlaneBMm!.Value, 2));
        var px = Math.Sqrt(Math.Pow((a.Twin.X - b.Twin.X) * size.Width, 2) + Math.Pow((a.Twin.Y - b.Twin.Y) * size.Height, 2));
        return mm >= MinMmGap && px >= MinPixelGap ? mm / px : null;
    }
}
