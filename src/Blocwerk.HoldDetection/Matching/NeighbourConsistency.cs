using Blocwerk.Core.Abstractions;

namespace Blocwerk.HoldDetection.Matching;

/// <summary>
/// Raise-only neighbour-consistency confidence pass. After the first matching pass, every
/// already-confident proposal is treated as a trusted local anchor. A candidate is boosted
/// when its image-to-image disparity agrees with the disparity of the confident anchors
/// around it AND its own colour/appearance/shape agree — two holds that are the same physical
/// hold seen from two viewpoints must share (almost) the same local disparity as their
/// confident neighbours. The boost can only raise a confidence (never lower it, never reach 1),
/// so precision is preserved: a wrong match one hold-spacing off gets little lift. Faithful
/// port of the validated Python <c>apply_neighbour_consistency</c>.
/// </summary>
internal static class NeighbourConsistency
{
    // Constants tuned on ~3024x4032 source frames. The three pixel-space constants
    // (R_ANCHOR, SPACE, NEIGH_TOL) are scaled by the actual image size at call time.
    private const double AnchorT = 0.60;
    private const double RAnchor = 1400.0;
    private const double Space = 700.0;
    private const double NeighTol = 32.0;
    private const double RelTol = 0.12;
    private const int MinAnchors = 6;
    private const double ColScale = 40.0;
    private const double Boost = 1.6;
    private const double ReferenceMaxDim = 4032.0;
    private const double Wc = 0.50, Wn = 0.30, Ws = 0.20;

    /// <summary>
    /// Mutates <paramref name="proposals"/>, replacing each confidence with its raise-only
    /// boosted value. <paramref name="diags"/> is aligned 1:1 with <paramref name="proposals"/>.
    /// </summary>
    /// <param name="proposals">Proposals from the first pass (indices are full hold indices).</param>
    /// <param name="diags">Per-proposal colour/appearance diagnostics, aligned to proposals.</param>
    /// <param name="left">Left holds (for optional sizes).</param>
    /// <param name="right">Right holds (for optional sizes).</param>
    /// <param name="cL">Left hold centres in pixels, indexed by full hold index.</param>
    /// <param name="cR">Right hold centres in pixels, indexed by full hold index.</param>
    /// <param name="maxDimL">max(width,height) of the left image.</param>
    /// <param name="maxDimR">max(width,height) of the right image.</param>
    public static void Apply(
        List<Proposal> proposals,
        IReadOnlyList<MatchDiag> diags,
        IReadOnlyList<MatcherHold> left,
        IReadOnlyList<MatcherHold> right,
        Pt[] cL,
        Pt[] cR,
        int maxDimL,
        int maxDimR)
    {
        double scale = (maxDimL + maxDimR) / (2.0 * ReferenceMaxDim);
        double rAnchor = RAnchor * scale;
        double space = Space * scale;
        double neighTol = NeighTol * scale;

        // Snapshot base confidences BEFORE any mutation so anchors and head-room use the
        // first-pass values, exactly like the reference.
        var baseConf = new double[proposals.Count];
        for (int k = 0; k < proposals.Count; k++)
        {
            baseConf[k] = proposals[k].Confidence;
        }

        var anchors = new List<(Pt AL, Pt AR, int LeftIdx)>();
        for (int k = 0; k < proposals.Count; k++)
        {
            Proposal p = proposals[k];
            if (baseConf[k] >= AnchorT && p.Rescue is null)
            {
                anchors.Add((cL[p.LeftIdx], cR[p.RightIdx], p.LeftIdx));
            }
        }

        for (int k = 0; k < proposals.Count; k++)
        {
            Proposal p = proposals[k];
            Pt pL = cL[p.LeftIdx];
            Pt pR = cR[p.RightIdx];
            double dpx = pR.X - pL.X;
            double dpy = pR.Y - pL.Y;

            double num = 0.0, den = 0.0;
            int neff = 0;
            foreach (var (aL, aR, aid) in anchors)
            {
                if (aid == p.LeftIdx)
                {
                    continue;
                }

                double dl = Math.Sqrt(((pL.X - aL.X) * (pL.X - aL.X)) + ((pL.Y - aL.Y) * (pL.Y - aL.Y)));
                if (dl > rAnchor)
                {
                    continue;
                }

                double dkx = aR.X - aL.X;
                double dky = aR.Y - aL.Y;
                double err = Math.Sqrt(((dpx - dkx) * (dpx - dkx)) + ((dpy - dky) * (dpy - dky)));
                double tol = neighTol + (RelTol * dl);
                double agree = Math.Exp(-((err / tol) * (err / tol)));
                double w = Math.Exp(-((dl / space) * (dl / space)));
                num += w * agree;
                den += w;
                neff++;
            }

            double support = den > 1e-9 ? num / den : 0.0;

            MatchDiag diag = diags[k];
            double col = Math.Clamp(1.0 - (diag.ColourDist / ColScale), 0.0, 1.0);
            double app = Math.Max(0.0, diag.AppearanceNcc);
            double shp = ShapeAgree(left[p.LeftIdx].SizeNorm, right[p.RightIdx].SizeNorm, maxDimL, maxDimR);
            double visual = (Wc * col) + (Wn * app) + (Ws * shp);
            double neigh = support * visual;

            double b = baseConf[k];
            // Raise-only, and capped below 1.0: Boost > 1 means b + Boost*neigh*(1-b) can exceed 1,
            // so clamp to [b, 0.99] — a proposal is always a user-confirmed suggestion, never "certain".
            double raw = neff >= MinAnchors ? b + (Boost * neigh * (1.0 - b)) : b;
            double boosted = Math.Max(b, Math.Min(0.99, raw));
            proposals[k] = p with { Confidence = boosted };
        }
    }

    private static double ShapeAgree(double? sizeL, double? sizeR, int maxDimL, int maxDimR)
    {
        if (sizeL is null || sizeR is null)
        {
            return 0.0;
        }

        double sideL = sizeL.Value * maxDimL;
        double sideR = sizeR.Value * maxDimR;
        double areaL = Math.Max(1.0, sideL * sideL);
        double areaR = Math.Max(1.0, sideR * sideR);
        double ratio = Math.Min(areaL, areaR) / Math.Max(areaL, areaR);
        return Math.Sqrt(ratio);
    }
}
