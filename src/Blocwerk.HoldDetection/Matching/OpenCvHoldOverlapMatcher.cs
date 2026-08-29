using Blocwerk.Core.Abstractions;
using OpenCvSharp;

namespace Blocwerk.HoldDetection.Matching;

/// <summary>
/// OpenCvSharp implementation of <see cref="IHoldOverlapMatcher"/>. Faithful in-process port
/// of the validated Python pipeline: coarse homography (overlap band only) → local warp field
/// L→R and R→L from spread anchors → geometric match with ambiguity margin + mutual-NN, with
/// appearance (patch NCC) and quantile-matched colour as light tie-breaks → greedy one-to-one
/// → conservative distinctive-colour rescue. SIFT is replaced by AKAZE for texture anchors.
/// </summary>
public sealed class OpenCvHoldOverlapMatcher : IHoldOverlapMatcher
{
    private const double GatePx = 60.0;
    private const double GeoScale = 45.0;

    /// <inheritdoc/>
    public HoldOverlapResult Match(
        byte[] leftImage,
        IReadOnlyList<MatcherHold> leftHolds,
        byte[] rightImage,
        IReadOnlyList<MatcherHold> rightHolds,
        HoldOverlapDirection direction)
    {
        using var imgL = Cv2.ImDecode(leftImage, ImreadModes.Color);
        using var imgR = Cv2.ImDecode(rightImage, ImreadModes.Color);
        if (imgL.Empty() || imgR.Empty())
        {
            throw new ArgumentException("Could not decode one of the wall images.");
        }

        var (h, _) = HomographyHelper.Coarse(imgL, imgR);
        if (h is null)
        {
            throw new InvalidOperationException("Coarse homography failed (too few texture matches).");
        }

        int wl = imgL.Width, hl = imgL.Height;
        int wr = imgR.Width, hr = imgR.Height;
        var cL = ToPixels(leftHolds, wl, hl);
        var cR = ToPixels(rightHolds, wr, hr);
        double[,] hInv = HomographyHelper.Invert(h);

        // Effective Lab colour per hold: caller-supplied when present, else sampled from the
        // decoded image inside the matcher so colour works whether or not the caller provides it.
        double[]?[] effL = LabSampling.Effective(imgL, leftHolds);
        double[]?[] effR = LabSampling.Effective(imgR, rightHolds);

        // Overlap band: a hold is in-band if its warped centre lands inside the other frame.
        const double m = 40.0;
        var li = BandIndices(cL, p => HomographyHelper.Warp(h, p), wr, hr, m);
        var ri = BandIndices(cR, p => HomographyHelper.Warp(hInv, p), wl, hl, m);
        var cLb = Gather(cL, li);
        var cRb = Gather(cR, ri);

        // Colour marginals across ALL holds → quantile map (R Lab → L Lab).
        var qmap = new ColorQuantileMap(LabMath.NonNull(effL), LabMath.NonNull(effR));

        // Band-local appearance descriptors.
        var descLb = Descriptors(imgL, leftHolds, li, Math.Max(wl, hl));
        var descRb = Descriptors(imgR, rightHolds, ri, Math.Max(wr, hr));

        // Local warp fields L→R (anchors) and R→L (same anchors reversed) for the mutual check.
        var build = WarpFieldBuilder.Build(imgL, imgR, cLb, cRb, descLb, descRb);
        var rfield = new LocalWarpField(build.AnchorsDst, build.AnchorsSrc);
        Pt[] predR = build.Field.Predict(cLb);
        Pt[] predL = rfield.Predict(cRb);

        // Nearest / second-nearest each direction → mutual NN + ambiguity margin.
        var nnR = predR.Select(p => MatchGeometry.KNearest(cRb, p, 2)).ToArray();
        var nnL = predL.Select(p => MatchGeometry.KNearest(cLb, p, 2)).ToArray();

        var (proposals, diags) = ScoreAndAssign(
            li, ri, descLb, descRb, qmap, predR, nnR, nnL, effL, effR);
        var usedL = new HashSet<int>(proposals.Select(p => p.LeftIdx));
        var usedR = new HashSet<int>(proposals.Select(p => p.RightIdx));

        ColorRescue(li, ri, cRb, descLb, descRb, qmap, predR, usedL, usedR, proposals, diags, effL, effR);

        // Raise-only neighbour-consistency pass: boost candidates whose disparity + colour/shape
        // agree with the confident anchors around them. Never lowers a confidence, never reaches 1.
        NeighbourConsistency.Apply(
            proposals, diags, leftHolds, rightHolds, cL, cR, Math.Max(wl, hl), Math.Max(wr, hr));

        var outProposals = proposals
            .Select(p => new HoldOverlapProposal(
                leftHolds[p.LeftIdx].Id, rightHolds[p.RightIdx].Id,
                Math.Round(p.Confidence, 3), p.Moved, Math.Round(p.ResidualPx, 1), p.Rescue))
            .OrderByDescending(p => p.Confidence)
            .ToList();

        var unmatchedL = li.Where(i => !usedL.Contains(i)).Select(i => leftHolds[i].Id).ToList();
        var unmatchedR = ri.Where(j => !usedR.Contains(j)).Select(j => rightHolds[j].Id).ToList();
        return new HoldOverlapResult(outProposals, unmatchedL, unmatchedR);
    }

    private static (List<Proposal> Proposals, List<MatchDiag> Diags) ScoreAndAssign(
        List<int> li, List<int> ri,
        List<float[]?> descLb, List<float[]?> descRb, ColorQuantileMap qmap,
        Pt[] predR, (int[] Idx, double[] Dist)[] nnR, (int[] Idx, double[] Dist)[] nnL,
        double[]?[] effL, double[]?[] effR)
    {
        var cand = new List<(double Conf, int A, int B, double Resid, double Dcol, double App)>();
        for (int a = 0; a < predR.Length; a++)
        {
            int b = nnR[a].Idx[0];
            double resid = nnR[a].Dist[0];
            if (resid >= GatePx)
            {
                continue;
            }

            double d2 = nnR[a].Dist.Length > 1 ? nnR[a].Dist[1] : resid;
            double margin = Math.Max(0.0, (d2 - resid) / (d2 + 1e-6));
            bool mutual = nnL[b].Idx[0] == a && nnL[b].Dist[0] < GatePx;

            int i = li[a], j = ri[b];
            double appearance = WarpFieldBuilder.Ncc(descLb[a], descRb[b]);
            double dcol = LabMath.Distance(effL[i], effR[j], qmap);
            double geo = Math.Exp(-Math.Pow(resid / GeoScale, 2));
            double col = Math.Max(0.0, 1 - (dcol / 45.0));
            double app = Math.Max(0.0, appearance);
            double conf = (0.30 * geo) + (0.27 * margin) + (0.18 * (mutual ? 1.0 : 0.0))
                          + (0.15 * app) + (0.10 * col);
            cand.Add((conf, a, b, resid, dcol, appearance));
        }

        cand.Sort((x, y) => y.Conf.CompareTo(x.Conf));
        var usedL = new HashSet<int>();
        var usedR = new HashSet<int>();
        var proposals = new List<Proposal>();
        var diags = new List<MatchDiag>();
        foreach (var c in cand)
        {
            int i = li[c.A], j = ri[c.B];
            if (usedL.Contains(i) || usedR.Contains(j))
            {
                continue;
            }

            usedL.Add(i);
            usedR.Add(j);
            proposals.Add(new Proposal(i, j, c.Conf, c.Resid > 0.6 * GatePx, c.Resid, null));
            diags.Add(new MatchDiag(c.Dcol, c.App));
        }

        return (proposals, diags);
    }

    private static void ColorRescue(
        List<int> li, List<int> ri, List<Pt> cRb,
        List<float[]?> descLb, List<float[]?> descRb, ColorQuantileMap qmap,
        Pt[] predR, HashSet<int> usedL, HashSet<int> usedR,
        List<Proposal> proposals, List<MatchDiag> diags,
        double[]?[] effL, double[]?[] effR)
    {
        const double colT = 13.0, rescueRad = 300.0;
        var labLb = li.Select(i => effL[i]).ToList();
        var labRbQ = ri.Select(j =>
        {
            double[]? v = effR[j];
            return v is null ? null : qmap.Apply(v);
        }).ToList();

        for (int a = 0; a < li.Count; a++)
        {
            int i = li[a];
            double[]? lc = labLb[a];
            if (usedL.Contains(i) || lc is null)
            {
                continue;
            }

            if (Math.Sqrt(Math.Pow(lc[1] - 128, 2) + Math.Pow(lc[2] - 128, 2)) < 28)
            {
                continue;
            }

            int sameL = labLb.Count(v => v is not null && LabMath.Norm(v, lc) < colT);
            if (sameL > 2)
            {
                continue;
            }

            var hits = new List<(int B, int J)>();
            for (int b = 0; b < ri.Count; b++)
            {
                int j = ri[b];
                double[]? rq = labRbQ[b];
                if (usedR.Contains(j) || rq is null)
                {
                    continue;
                }

                if (LabMath.Norm(lc, rq) < colT && cRb[b].Dist(predR[a]) < rescueRad)
                {
                    hits.Add((b, j));
                }
            }

            if (hits.Count != 1)
            {
                continue;
            }

            var (bb, jj) = hits[0];
            usedL.Add(i);
            usedR.Add(jj);
            double rescueDcol = LabMath.Norm(lc, labRbQ[bb]!);
            double rescueApp = WarpFieldBuilder.Ncc(descLb[a], descRb[bb]);
            proposals.Add(new Proposal(i, jj, 0.35, false, cRb[bb].Dist(predR[a]), "colour"));
            diags.Add(new MatchDiag(rescueDcol, rescueApp));
        }
    }

    private static Pt[] ToPixels(IReadOnlyList<MatcherHold> holds, int w, int hgt)
    {
        var pts = new Pt[holds.Count];
        for (int i = 0; i < holds.Count; i++)
        {
            pts[i] = new Pt(holds[i].X * w, holds[i].Y * hgt);
        }

        return pts;
    }

    private static List<int> BandIndices(Pt[] centres, Func<Pt, Pt> warp, int w, int hgt, double margin)
    {
        var idx = new List<int>();
        for (int i = 0; i < centres.Length; i++)
        {
            Pt p = warp(centres[i]);
            if (p.X > -margin && p.X < w + margin && p.Y > -margin && p.Y < hgt + margin)
            {
                idx.Add(i);
            }
        }

        return idx;
    }

    private static List<Pt> Gather(Pt[] centres, List<int> idx)
    {
        var outp = new List<Pt>(idx.Count);
        foreach (int i in idx)
        {
            outp.Add(centres[i]);
        }

        return outp;
    }

    private static List<float[]?> Descriptors(Mat img, IReadOnlyList<MatcherHold> holds, List<int> idx, int maxDim)
    {
        var descs = new List<float[]?>(idx.Count);
        foreach (int i in idx)
        {
            MatcherHold hld = holds[i];
            if (hld.SizeNorm is null)
            {
                descs.Add(null);
                continue;
            }

            double sizePx = hld.SizeNorm.Value * maxDim;
            descs.Add(MatchAppearance.Describe(img, hld.X * img.Width, hld.Y * img.Height, sizePx));
        }

        return descs;
    }
}
