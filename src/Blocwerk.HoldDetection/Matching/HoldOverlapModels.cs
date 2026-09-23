namespace Blocwerk.HoldDetection.Matching;

/// <summary>Internal working proposal carrying band-array indices (not hold ids) during assignment.</summary>
internal readonly record struct Proposal(
    int LeftIdx, int RightIdx, double Confidence, bool Moved, double ResidualPx, string? Rescue);

/// <summary>
/// Per-proposal visual diagnostics captured during scoring and reused by the raise-only
/// neighbour-consistency pass: quantile-matched Lab colour distance and appearance NCC.
/// </summary>
/// <param name="ColourDist">Lab distance between the pair after quantile matching (40 when unknown).</param>
/// <param name="AppearanceNcc">Patch normalised cross-correlation for the pair.</param>
internal readonly record struct MatchDiag(double ColourDist, double AppearanceNcc);

/// <summary>A primary-assignment candidate (band indices) that cleared the residual and colour gates.</summary>
internal readonly record struct Candidate(
    int A, int B, double Resid, double Margin, bool Mutual, double App, double Dcol);

/// <summary>Diagnostic counters of one primary assignment — instrumentation only, never read back by the matcher.</summary>
internal sealed class MatchStats
{
    /// <summary>Residuals of every candidate that cleared the residual gate.</summary>
    public List<double> Residuals { get; } = new();

    /// <summary>Candidates beyond the residual gate.</summary>
    public int GatedOut { get; set; }

    /// <summary>Candidates rejected by the colour sanity-gate.</summary>
    public int ColourGated { get; set; }

    /// <summary>Candidates whose predicted centre falls off the detection (see <see cref="MatchGate"/>).</summary>
    public int OffHold { get; set; }

    /// <summary>Assigned (mutual) proposals.</summary>
    public int MutualNn { get; set; }
}
