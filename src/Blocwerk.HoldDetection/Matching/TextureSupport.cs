namespace Blocwerk.HoldDetection.Matching;

/// <summary>
/// Ties a proposal's confidence to INDEPENDENT geometric evidence. The first-pass confidence and the
/// neighbour-consistency boost both measure agreement with the warp field — which a wrong field (two photos
/// sharing almost no texture, a bootstrap that confirmed itself) satisfies just as well as a right one. What a
/// wrong field lacks is measured anchors: texture correspondences or marker/wall-space pairs near the hold.
/// A proposal with none around it is kept, but capped below the auto-accept tier so a person confirms it.
/// </summary>
internal static class TextureSupport
{
    /// <summary>Radius, as a fraction of the left image's longer side, within which anchors support a hold (≈200 px on 4000 px).</summary>
    internal const double RadiusFraction = 0.05;

    /// <summary>Fewest measured anchors within the radius for a proposal to keep its confidence.</summary>
    internal const int MinAnchors = 1;

    /// <summary>Confidence ceiling of an unsupported proposal — the confirm tier, below the 0.45 auto-accept threshold.</summary>
    internal const double UnsupportedCap = 0.40;

    /// <summary>Whether a left-image point has at least <see cref="MinAnchors"/> measured anchors within the support radius.</summary>
    /// <param name="point">Left-image point, in pixels.</param>
    /// <param name="measured">Measured anchor positions in the left image.</param>
    /// <param name="maxDimLeft">max(width, height) of the left image.</param>
    public static bool IsSupported(Pt point, IReadOnlyList<Pt> measured, int maxDimLeft)
    {
        double r2 = Math.Pow(RadiusFraction * maxDimLeft, 2);
        int support = 0;
        for (int m = 0; m < measured.Count && support < MinAnchors; m++)
        {
            if (measured[m].Dist2(point) < r2)
            {
                support++;
            }
        }

        return support >= MinAnchors;
    }

    /// <summary>Caps every proposal without measured support nearby; returns how many were capped.</summary>
    /// <param name="proposals">Proposals (full left-hold indices); confidences are lowered in place.</param>
    /// <param name="leftCentres">Left hold centres in pixels, indexed by full hold index.</param>
    /// <param name="measured">Measured anchor positions in the left image.</param>
    /// <param name="maxDimLeft">max(width, height) of the left image.</param>
    public static int CapUnsupported(List<Proposal> proposals, Pt[] leftCentres, IReadOnlyList<Pt> measured, int maxDimLeft)
    {
        int capped = 0;
        for (int k = 0; k < proposals.Count; k++)
        {
            if (proposals[k].Confidence > UnsupportedCap && !IsSupported(leftCentres[proposals[k].LeftIdx], measured, maxDimLeft))
            {
                proposals[k] = proposals[k] with { Confidence = UnsupportedCap };
                capped++;
            }
        }

        return capped;
    }
}
