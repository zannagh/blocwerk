using Blocwerk.Core.Abstractions;

namespace Blocwerk.HoldDetection.Matching;

/// <summary>
/// Extra warp-field anchors from a <see cref="HoldOverlapSeed"/>, in pixels. <b>Fixed</b> anchors are
/// measurements (shared marker corners, wall-space hold anchors) and always join the field. <b>Fill</b>
/// anchors are predictions (plane-induced hold positions, or a grid through the seed homography) that are
/// off by relief parallax, so they only fill regions no real anchor covers within <see cref="FillRadiusPx"/>.
/// </summary>
internal sealed record WarpPrior(
    IReadOnlyList<Pt> FixedSrc,
    IReadOnlyList<Pt> FixedDst,
    IReadOnlyList<Pt> FillSrc,
    IReadOnlyList<Pt> FillDst,
    double FillRadiusPx)
{
    /// <summary>Fill radius as a fraction of the left image's longer side (≈120 px on a 4000 px photo).</summary>
    internal const double FillRadiusFraction = 0.03;

    /// <summary>
    /// Builds the prior for a seeded run (anchor ids resolve to their holds' centres). Measured pairs and
    /// anchors always join; the homography-derived fill only when <paramref name="homographyTrusted"/>
    /// (the coarse step chose the seed), since a single-marker seed's predictions degrade away from it.
    /// </summary>
    public static WarpPrior From(
        HoldOverlapSeed seed,
        bool homographyTrusted,
        IReadOnlyList<MatcherHold> leftHolds,
        IReadOnlyList<MatcherHold> rightHolds,
        int wl, int hl, int wr, int hr)
    {
        var fixedSrc = new List<Pt>();
        var fixedDst = new List<Pt>();
        foreach (var p in seed.MeasuredPairs)
        {
            fixedSrc.Add(new Pt(p.LeftX * wl, p.LeftY * hl));
            fixedDst.Add(new Pt(p.RightX * wr, p.RightY * hr));
        }

        var leftById = IndexById(leftHolds);
        var rightById = IndexById(rightHolds);
        foreach (var a in seed.Anchors)
        {
            if (leftById.TryGetValue(a.LeftHoldId, out var l) && rightById.TryGetValue(a.RightHoldId, out var r))
            {
                fixedSrc.Add(new Pt(l.X * wl, l.Y * hl));
                fixedDst.Add(new Pt(r.X * wr, r.Y * hr));
            }
        }

        var (fillSrc, fillDst) = homographyTrusted ? Fill(seed, wl, hl, wr, hr) : (new List<Pt>(), new List<Pt>());
        return new WarpPrior(fixedSrc, fixedDst, fillSrc, fillDst, FillRadiusFraction * Math.Max(wl, hl));
    }

    /// <summary>The base anchors plus every fill anchor with no base anchor within <see cref="FillRadiusPx"/>.</summary>
    public (List<Pt> Src, List<Pt> Dst) WithFill(List<Pt> src, List<Pt> dst)
    {
        var outSrc = new List<Pt>(src);
        var outDst = new List<Pt>(dst);
        for (int i = 0; i < FillSrc.Count; i++)
        {
            bool covered = false;
            foreach (var s in src)
            {
                if (s.Dist(FillSrc[i]) < FillRadiusPx)
                {
                    covered = true;
                    break;
                }
            }

            if (!covered)
            {
                outSrc.Add(FillSrc[i]);
                outDst.Add(FillDst[i]);
            }
        }

        return (outSrc, outDst);
    }

    private static (List<Pt> Src, List<Pt> Dst) Fill(HoldOverlapSeed seed, int wl, int hl, int wr, int hr)
    {
        var src = new List<Pt>();
        var dst = new List<Pt>();
        if (seed.PriorPairs.Count > 0)
        {
            foreach (var p in seed.PriorPairs)
            {
                src.Add(new Pt(p.LeftX * wl, p.LeftY * hl));
                dst.Add(new Pt(p.RightX * wr, p.RightY * hr));
            }

            return (src, dst);
        }

        if (seed.Homography is null)
        {
            return (src, dst);
        }

        // No per-hold priors (no wall model): a coarse grid through the seed homography, kept only where
        // it lands inside the right photo.
        var h = SeededCoarse.ToPixels(seed.Homography, wl, hl, wr, hr);
        for (int i = 1; i < 16; i++)
        {
            for (int j = 1; j < 12; j++)
            {
                var p = new Pt(wl * i / 16.0, hl * j / 12.0);
                var q = HomographyHelper.Warp(h, p);
                if (double.IsFinite(q.X) && double.IsFinite(q.Y) && q.X >= 0 && q.X < wr && q.Y >= 0 && q.Y < hr)
                {
                    src.Add(p);
                    dst.Add(q);
                }
            }
        }

        return (src, dst);
    }

    private static Dictionary<int, MatcherHold> IndexById(IReadOnlyList<MatcherHold> holds)
    {
        var map = new Dictionary<int, MatcherHold>();
        foreach (var h in holds)
        {
            map.TryAdd(h.Id, h);
        }

        return map;
    }
}
