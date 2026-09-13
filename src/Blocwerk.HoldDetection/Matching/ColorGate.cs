namespace Blocwerk.HoldDetection.Matching;

/// <summary>
/// Colour sanity-gate for the primary carryover assignment. A match candidate is hard-rejected
/// when the old↔new quantile-mapped Lab distance (the SAME <see cref="LabMath.Distance"/> used for
/// the soft colour confidence term) exceeds <see cref="ColorGateDeltaE"/>. Deliberately lenient:
/// only clear mismatches trip it (e.g. an old marker on bare wood paired to a blue hold), so a
/// rechalked / differently-lit hold of the same underlying colour still matches. The gate is only
/// consulted when BOTH holds carry a colour sample — a missing colour never causes a rejection.
/// </summary>
internal static class ColorGate
{
    /// <summary>
    /// Hard rejection threshold on the quantile-mapped Lab ΔE between a candidate's old and new hold.
    /// Aligned with the soft term (<c>col = max(0, 1 - dcol/45)</c>), which already reaches 0 here, so
    /// the gate only kills pairs the soft term had already discounted entirely.
    /// </summary>
    internal const double ColorGateDeltaE = 70.0;

    /// <summary>
    /// Returns true when the candidate should be rejected on colour: both colours are present AND their
    /// quantile-mapped Lab distance exceeds <see cref="ColorGateDeltaE"/>. Returns false (do not reject)
    /// whenever either colour is missing, so an unknown colour never blocks a geometrically-sound match.
    /// </summary>
    /// <param name="leftLab">Old-hold effective Lab colour, or null when unsampled.</param>
    /// <param name="rightLab">New-hold effective Lab colour, or null when unsampled.</param>
    /// <param name="quantileMappedDeltaE">The pair's quantile-mapped Lab distance (from <see cref="LabMath.Distance"/>).</param>
    internal static bool Rejects(double[]? leftLab, double[]? rightLab, double quantileMappedDeltaE)
    {
        if (leftLab is null || rightLab is null)
        {
            return false;
        }

        return quantileMappedDeltaE > ColorGateDeltaE;
    }
}
