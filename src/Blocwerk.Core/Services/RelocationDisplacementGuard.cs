// <copyright file="RelocationDisplacementGuard.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Services;

/// <summary>A matched carry on the staged photo: where its twin sits (RAW pixels) and the matcher's residual.</summary>
public readonly record struct ResidualAnchor(double X, double Y, double ResidualPx);

/// <summary>
/// Drops "Possibly moved" suggestions whose jump cannot be a hold shifted on the wall: the distance from
/// where the alignment puts the vanished hold to the look-alike detection must stay within the local
/// alignment noise (a multiple of the neighbouring matches' residuals) or a physical limit. A look-alike
/// across the wall is a different hold, not the same one moved.
/// </summary>
public static class RelocationDisplacementGuard
{
    /// <summary>
    /// The farthest a hold may plausibly have moved, as a fraction of the photo's longer side (about
    /// 150-250 mm on a panel photo).
    /// </summary>
    public const double MaxShiftFraction = 0.04;

    public const double ResidualFactor = 3;

    private const int NearestAnchors = 8;

    /// <summary>The pixel distance a suggestion may span at the expected position.</summary>
    public static double AllowedShiftPx(
        (double X, double Y) expected, IReadOnlyList<ResidualAnchor> anchors, int longerSidePx)
    {
        var physical = MaxShiftFraction * longerSidePx;
        var near = anchors
            .OrderBy(a => Math.Pow(a.X - expected.X, 2) + Math.Pow(a.Y - expected.Y, 2))
            .Take(NearestAnchors)
            .Select(a => a.ResidualPx)
            .OrderBy(r => r)
            .ToList();
        var noise = near.Count == 0 ? 0 : ResidualFactor * near[near.Count / 2];
        return Math.Max(noise, physical);
    }

    /// <summary>
    /// Keeps the pairs whose displacement is plausible. A pair whose expected position is unknown (the
    /// matcher could not predict the vanished hold) is kept: there is nothing to judge it by.
    /// </summary>
    public static List<RelocationPair> Filter(
        IReadOnlyList<RelocationPair> pairs,
        IReadOnlyDictionary<Guid, (double X, double Y)> expectedPx,
        IReadOnlyDictionary<Guid, (double X, double Y)> newPx,
        IReadOnlyList<ResidualAnchor> anchors,
        int longerSidePx)
    {
        return pairs.Where(p =>
        {
            if (!expectedPx.TryGetValue(p.OldHoldId, out var expected) || !newPx.TryGetValue(p.NewHoldId, out var actual))
            {
                return true;
            }

            var shift = Math.Sqrt(Math.Pow(actual.X - expected.X, 2) + Math.Pow(actual.Y - expected.Y, 2));
            return shift <= AllowedShiftPx(expected, anchors, longerSidePx);
        }).ToList();
    }
}
