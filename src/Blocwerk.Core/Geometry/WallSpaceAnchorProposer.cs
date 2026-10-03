using Blocwerk.Core.Abstractions;

namespace Blocwerk.Core.Geometry;

/// <summary>A hold as the wall-space anchor rule sees it. Null metric fields mean "not measured".</summary>
/// <param name="Index">The caller's (matcher) id of the hold.</param>
/// <param name="FacetId">The facet the plane position is on.</param>
/// <param name="PlaneAMm">Plane u coordinate, mm.</param>
/// <param name="PlaneBMm">Plane v coordinate, mm.</param>
/// <param name="SizeMm">max(width, height) in mm, when measured.</param>
/// <param name="Fingerprint">Colour/shape descriptor.</param>
public sealed record WallSpaceHold(
    int Index, string? FacetId, double? PlaneAMm, double? PlaneBMm, double? SizeMm, HoldFingerprint? Fingerprint);

/// <summary>
/// Proposes confident same-hold pairs from wall-plane positions. Built around what the plane position
/// of a hold measured from ONE photo is worth: the hold stands proud of the wall and each camera projects
/// that relief to a different plane spot, so two photos disagree by ~32 mm median, p90 ~100 mm (volumes
/// ~130 mm). A tight radius would therefore miss most twins; instead the radius is generous and the
/// FINGERPRINT decides, with mutual-nearest-neighbour and an ambiguity margin on top. Precision over
/// recall: a missed anchor costs nothing, a wrong one bends the warp field.
/// </summary>
public static class WallSpaceAnchorProposer
{
    /// <summary>Floor of the search radius, mm (≈ 2× the median two-photo disagreement).</summary>
    public const double MinToleranceMm = 60.0;

    /// <summary>Ceiling of the search radius, mm (≈ p90 disagreement + volume headroom).</summary>
    public const double MaxToleranceMm = 150.0;

    /// <summary>Radius per mm of hold size (big holds/volumes have more relief, hence more parallax).</summary>
    public const double SizeFactor = 1.0;

    /// <summary>Minimum fingerprint similarity of an anchor.</summary>
    public const double MinSimilarity = 0.6;

    /// <summary>Minimum lead of the best candidate's score over the runner-up, on BOTH sides.</summary>
    public const double MinMargin = 0.08;

    /// <summary>
    /// Search radius for a pair. Uses the SMALLER measured size of the two: a manual hold's size is often a
    /// placeholder (radius 0.003 ≈ 56 mm) and must not inflate the radius.
    /// </summary>
    public static double ToleranceMm(double? leftSizeMm, double? rightSizeMm)
    {
        double? size = leftSizeMm is > 0 && rightSizeMm is > 0
            ? Math.Min(leftSizeMm.Value, rightSizeMm.Value)
            : null;
        return Math.Clamp(Math.Max(MinToleranceMm, SizeFactor * (size ?? 0)), MinToleranceMm, MaxToleranceMm);
    }

    /// <summary>Proposes anchors, best first.</summary>
    /// <param name="left">Left holds.</param>
    /// <param name="right">Right holds.</param>
    /// <returns>One-to-one anchor pairs.</returns>
    public static IReadOnlyList<HoldOverlapAnchor> Propose(IReadOnlyList<WallSpaceHold> left, IReadOnlyList<WallSpaceHold> right)
    {
        var l = left.Where(Eligible).ToList();
        var r = right.Where(Eligible).ToList();
        var candidates = new List<Candidate>();
        foreach (var a in l)
        {
            foreach (var b in r)
            {
                if (Score(a, b) is { } c)
                {
                    candidates.Add(c);
                }
            }
        }

        var byLeft = candidates.ToLookup(c => c.Left.Index);
        var byRight = candidates.ToLookup(c => c.Right.Index);
        var anchors = new List<HoldOverlapAnchor>();
        foreach (var c in candidates)
        {
            if (IsClearWinner(c, byLeft[c.Left.Index]) && IsClearWinner(c, byRight[c.Right.Index]))
            {
                anchors.Add(new HoldOverlapAnchor(c.Left.Index, c.Right.Index, Math.Round(c.DistanceMm, 1), Math.Round(c.Similarity, 3)));
            }
        }

        return anchors.OrderByDescending(a => a.Similarity).ToList();
    }

    private static bool Eligible(WallSpaceHold h) =>
        h.FacetId is not null && h.PlaneAMm is not null && h.PlaneBMm is not null && h.Fingerprint is not null;

    private static Candidate? Score(WallSpaceHold a, WallSpaceHold b)
    {
        if (!string.Equals(a.FacetId, b.FacetId, StringComparison.Ordinal))
        {
            return null;
        }

        double d = Math.Sqrt(Math.Pow(a.PlaneAMm!.Value - b.PlaneAMm!.Value, 2) + Math.Pow(a.PlaneBMm!.Value - b.PlaneBMm!.Value, 2));
        double tol = ToleranceMm(a.SizeMm, b.SizeMm);
        if (d > tol)
        {
            return null;
        }

        double sim = HoldFingerprint.Similarity(a.Fingerprint!, b.Fingerprint!);
        if (sim < MinSimilarity)
        {
            return null;
        }

        // Appearance dominates; distance only breaks near-ties (it is noisy by ~tol/2 by construction).
        return new Candidate(a, b, d, sim, sim - (0.15 * d / tol));
    }

    /// <summary>The candidate is the best of its group and leads the runner-up by <see cref="MinMargin"/>.</summary>
    private static bool IsClearWinner(Candidate c, IEnumerable<Candidate> group)
    {
        foreach (var other in group)
        {
            if (!ReferenceEquals(other, c) && other.Score > c.Score - MinMargin)
            {
                return false;
            }
        }

        return true;
    }

    private sealed record Candidate(WallSpaceHold Left, WallSpaceHold Right, double DistanceMm, double Similarity, double Score);
}
