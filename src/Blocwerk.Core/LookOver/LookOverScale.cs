// <copyright file="LookOverScale.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.LookOver;

/// <summary>
/// How much bigger (or smaller) the wall is in the new photo than in the old one around one spot, in pixels per pixel:
/// what makes the "before" and the "now" picture of a finding show the same piece of wall although the two photos were
/// framed or sized differently. Measured on the holds the update carried nearby, which are known in both photos. Pure.
/// </summary>
internal static class LookOverScale
{
    /// <summary>How many of the nearest carried holds are measured.</summary>
    public const int Neighbours = 8;

    /// <summary>With fewer carried holds nearby there is no scale (the pictures fall back to their own).</summary>
    public const int MinNeighbours = 3;

    /// <summary>A neighbour closer than this (px, old photo) says more about its own detection jitter than about scale.</summary>
    public const double MinDistancePx = 40;

    /// <summary>The local scale old photo to new photo at a spot, or null when it cannot be told reliably.</summary>
    /// <param name="spotOld">The spot on the old photo, px.</param>
    /// <param name="spotNew">The same spot on the new photo, px.</param>
    /// <param name="carried">Holds known in both photos: their position on the old and on the new one, px.</param>
    /// <returns>New pixels per old pixel.</returns>
    public static double? Local(
        (double X, double Y) spotOld, (double X, double Y) spotNew, IEnumerable<((double X, double Y) Old, (double X, double Y) New)> carried)
    {
        var ratios = carried
            .Select(c => (Old: Distance(c.Old, spotOld), New: Distance(c.New, spotNew)))
            .Where(d => d.Old >= MinDistancePx)
            .OrderBy(d => d.Old)
            .Take(Neighbours)
            .Select(d => d.New / d.Old)
            .Order()
            .ToList();
        if (ratios.Count < MinNeighbours)
        {
            return null;
        }

        // The median: one hold that really moved must not bend the scale. Anything wild is no scale at all.
        var median = ratios[ratios.Count / 2];
        return median is >= 0.25 and <= 4 ? median : null;
    }

    private static double Distance((double X, double Y) a, (double X, double Y) b) =>
        Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));
}
