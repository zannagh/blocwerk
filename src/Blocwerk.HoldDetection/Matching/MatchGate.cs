using Blocwerk.Core.Abstractions;

namespace Blocwerk.HoldDetection.Matching;

/// <summary>
/// "Is this detection really the old hold, or just the nearest thing next to it?" — the geometric gate of
/// the primary assignment. The typical carryover mispair is a hold the detector MISSED on the new photo:
/// the warp field still predicts its position correctly, and plain nearest-neighbour then snaps it onto a
/// neighbouring detection one hold-spacing away. Such a pair is recognisable without any colour: the
/// predicted centre lands OUTSIDE the candidate detection, by far more than the field's own local error.
/// Declining it costs little — the unmatched old hold is warp-carried to its predicted position.
/// </summary>
internal static class MatchGate
{
    /// <summary>A candidate's residual may reach this many detection radii (the predicted centre lies on the hold).</summary>
    internal const double RadiusFactor = 1.0;

    /// <summary>...or this many times the local field error, whichever is larger (parallax-heavy regions stay lenient).</summary>
    internal const double SpreadFactor = 2.0;

    /// <summary>Floor on the local field error, so a near-perfect field does not shrink the tolerance to nothing.</summary>
    internal const double MinSpreadPx = 4.0;

    /// <summary>
    /// Sizes (each normalized to its own photo's long side) further apart than this factor are not the same hold: a hold
    /// on a volume and the volume lie on one spot. Generous, so a photo framed a little differently never trips it.
    /// </summary>
    internal const double OtherSizeFactor = 2.5;

    /// <summary>
    /// The next detection from the predicted centre must lie at least this fraction farther than the chosen one; nearer, the
    /// pick is a toss-up between two neighbours (see <see cref="Ambiguous"/>).
    /// </summary>
    internal const double MinUniqueMargin = 0.25;

    /// <summary>Within this many pixels (or half the detection's radius, when more) the predicted centre is on the detection.</summary>
    internal const double OnHoldPx = 12.0;

    /// <summary>Number of neighbouring mutual candidates the local field error is estimated from.</summary>
    internal const int SpreadNeighbours = 8;

    /// <summary>
    /// The local field error around each candidate: the median residual of its <see cref="SpreadNeighbours"/>
    /// nearest OTHER mutual candidates (by new-image position). Infinity where fewer than three exist, so the
    /// gate never fires on an unestimable neighbourhood.
    /// </summary>
    /// <param name="positions">New-image position of every candidate.</param>
    /// <param name="residuals">Residual of every candidate (aligned with <paramref name="positions"/>).</param>
    /// <param name="mutual">Whether each candidate is a mutual nearest neighbour.</param>
    public static double[] LocalSpread(IReadOnlyList<Pt> positions, IReadOnlyList<double> residuals, IReadOnlyList<bool> mutual)
    {
        var refs = new List<int>();
        for (int k = 0; k < positions.Count; k++)
        {
            if (mutual[k])
            {
                refs.Add(k);
            }
        }

        var spread = new double[positions.Count];
        for (int k = 0; k < positions.Count; k++)
        {
            var near = refs
                .Where(r => r != k)
                .OrderBy(r => positions[r].Dist2(positions[k]))
                .Take(SpreadNeighbours)
                .Select(r => residuals[r])
                .OrderBy(v => v)
                .ToArray();
            spread[k] = near.Length < 3 ? double.PositiveInfinity : near[near.Length / 2];
        }

        return spread;
    }

    /// <summary>
    /// A hold's size for <see cref="OtherSize"/>, normalized to its photo's long side: half the larger extent of its
    /// outline when it has one (a hand-drawn hold's radius need not follow its outline), else its radius.
    /// </summary>
    /// <param name="hold">The hold.</param>
    /// <param name="width">Its photo's width, px.</param>
    /// <param name="height">Its photo's height, px.</param>
    public static double? SizeOf(MatcherHold hold, double width, double height)
    {
        if (hold.Shape is not { Count: >= 3 } shape || width <= 0 || height <= 0)
        {
            return hold.SizeNorm;
        }

        double w = (shape.Max(p => p.X) - shape.Min(p => p.X)) * width;
        double h = (shape.Max(p => p.Y) - shape.Min(p => p.Y)) * height;
        return Math.Max(w, h) / 2 / Math.Max(width, height);
    }

    /// <summary>True when both sizes are known and differ by more than <see cref="OtherSizeFactor"/>.</summary>
    /// <param name="oldSize">The old hold's normalized size, or null.</param>
    /// <param name="newSize">The detection's normalized size, or null.</param>
    public static bool OtherSize(double? oldSize, double? newSize) =>
        oldSize is > 0 && newSize is > 0 && Math.Max(oldSize.Value, newSize.Value) > OtherSizeFactor * Math.Min(oldSize.Value, newSize.Value);

    /// <summary>
    /// The nearest candidate of an old hold that is not of a clearly different size: the nearest one, else the second
    /// nearest when the nearest is (the old hold's base, or a hold sitting on it), else none.
    /// </summary>
    /// <param name="oldSize">The old hold's normalized size, or null.</param>
    /// <param name="candidateSizes">The normalized sizes of its nearest detections, nearest first.</param>
    /// <returns>The position in <paramref name="candidateSizes"/>, or -1.</returns>
    public static int NearestOfItsSize(double? oldSize, IReadOnlyList<double?> candidateSizes)
    {
        for (int n = 0; n < candidateSizes.Count; n++)
        {
            if (!OtherSize(oldSize, candidateSizes[n]))
            {
                return n;
            }
        }

        return -1;
    }

    /// <summary>
    /// True when the predicted centre falls off the candidate detection: the residual exceeds both
    /// <see cref="RadiusFactor"/> detection radii and <see cref="SpreadFactor"/>× the local field error.
    /// Never rejects when the detection has no size.
    /// </summary>
    /// <param name="residualPx">Distance between the warp-predicted old centre and the detection, in new-image pixels.</param>
    /// <param name="radiusPx">The detection's radius in new-image pixels, or null when unknown.</param>
    /// <param name="localSpreadPx">The local field error from <see cref="LocalSpread"/>.</param>
    public static bool OffHold(double residualPx, double? radiusPx, double localSpreadPx)
    {
        if (radiusPx is not { } r || r <= 0)
        {
            return false;
        }

        double tolerance = Math.Max(RadiusFactor * r, SpreadFactor * Math.Max(localSpreadPx, MinSpreadPx));
        return residualPx > tolerance;
    }

    /// <summary>
    /// True when the pick is not unique: the predicted centre is not on the chosen detection (farther than
    /// <see cref="OnHoldPx"/> or half its radius) and the next detection is less than <see cref="MinUniqueMargin"/> farther.
    /// Such a pair is how an old hold "jumps" to a neighbouring detection, and its displacement is then reported as a move.
    /// On The Attic's gen 5 → 6 update all 8 such pairs of 961 were false moves. Declining costs little: the old hold is
    /// warp-carried, and the detection is triaged as a new one (a second outline of a carried hold is discarded there).
    /// </summary>
    /// <param name="residualPx">Distance from the predicted centre to the chosen detection, px.</param>
    /// <param name="competitorPx">Distance to the nearest other detection, px (infinity when there is none).</param>
    /// <param name="radiusPx">The chosen detection's radius, px, or null.</param>
    public static bool Ambiguous(double residualPx, double competitorPx, double? radiusPx)
    {
        if (residualPx <= Math.Max(OnHoldPx, 0.5 * (radiusPx ?? 0)) || double.IsPositiveInfinity(competitorPx))
        {
            return false;
        }

        return competitorPx <= 0 || (competitorPx - residualPx) / competitorPx < MinUniqueMargin;
    }

    /// <summary>
    /// The detection an old hold is offered, as its band index and residual, or null when it is gated out: none of its
    /// own size near, beyond the residual gate, or not unique (another detection about as near, <see cref="MatchGate.Ambiguous"/>).
    /// </summary>
    /// <param name="nearest">The old hold's nearest detections (band indices) and their distances from its predicted centre.</param>
    /// <param name="oldSize">The old hold's normalized size, or null.</param>
    /// <param name="rightSizes">The detections' normalized sizes, by band index.</param>
    /// <param name="radiusRb">The detections' radii in pixels, by band index.</param>
    /// <param name="gatePx">The residual gate, px.</param>
    /// <param name="requireUnique">Whether a pick that is not unique is declined (the carryover pass, not the overlap one).</param>
    /// <param name="stats">Counts every candidate gated out, by why.</param>
    public static (int B, double Resid)? Pick(
        (int[] Idx, double[] Dist) nearest, double? oldSize, List<double?> rightSizes, List<double?> radiusRb, double gatePx, bool requireUnique, MatchStats stats)
    {
        // A hold on a volume and the volume share a spot: the nearest detection of the old hold's own size is asked.
        int n = NearestOfItsSize(oldSize, nearest.Idx.Select(c => rightSizes[c]).ToList());
        if (n != 0)
        {
            stats.OtherSize++;
        }

        if (n < 0)
        {
            return null;
        }

        int b = nearest.Idx[n];
        double resid = nearest.Dist[n];
        if (resid >= gatePx)
        {
            stats.GatedOut++;
            return null;
        }

        if (requireUnique && Ambiguous(resid, Competitor(nearest, n, oldSize, rightSizes), radiusRb[b]))
        {
            stats.Ambiguous++;
            return null;
        }

        return (b, resid);
    }

    /// <summary>
    /// The distance to the nearest OTHER detection that could be the old hold too: one of its own size. A volume the hold sits
    /// on, or a small hold on a volume, lies on the same spot but is no rival, so it never makes a pick ambiguous.
    /// </summary>
    /// <param name="nearest">The nearest detections and their distances.</param>
    /// <param name="picked">The position of the pick in <paramref name="nearest"/>.</param>
    /// <param name="oldSize">The old hold's normalized size, or null.</param>
    /// <param name="rightSizes">The detections' normalized sizes, by band index.</param>
    /// <returns>The distance, or infinity when there is no such rival.</returns>
    internal static double Competitor((int[] Idx, double[] Dist) nearest, int picked, double? oldSize, List<double?> rightSizes)
    {
        var best = double.PositiveInfinity;
        for (int k = 0; k < nearest.Idx.Length; k++)
        {
            if (k != picked && !OtherSize(oldSize, rightSizes[nearest.Idx[k]]))
            {
                best = Math.Min(best, nearest.Dist[k]);
            }
        }

        return best;
    }
}
