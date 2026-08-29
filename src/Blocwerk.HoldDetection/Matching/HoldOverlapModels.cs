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
