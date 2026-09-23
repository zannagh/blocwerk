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
}
