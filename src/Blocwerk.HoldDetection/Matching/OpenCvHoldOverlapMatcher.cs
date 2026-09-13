using Blocwerk.Core.Abstractions;
using Microsoft.Extensions.Logging;
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
    private const double GatePx = 90.0;
    private const double GeoScale = 45.0;

    /// <inheritdoc/>
    public HoldOverlapResult Match(
        byte[] leftImage,
        IReadOnlyList<MatcherHold> leftHolds,
        byte[] rightImage,
        IReadOnlyList<MatcherHold> rightHolds,
        HoldOverlapDirection direction,
        ILogger? diag = null)
    {
        using var imgL = Cv2.ImDecode(leftImage, ImreadModes.Color);
        using var imgR = Cv2.ImDecode(rightImage, ImreadModes.Color);
        if (imgL.Empty() || imgR.Empty())
        {
            throw new ArgumentException("Could not decode one of the wall images.");
        }

        var coarse = HomographyHelper.Coarse(imgL, imgR);
        if (coarse.H is null)
        {
            // Log the homography counts before the catastrophic run propagates, so the
            // failure is diagnosable in the app log rather than just an opaque exception.
            diag?.LogInformation(
                "[Matcher] homography ka={KaKeypoints} kb={KbKeypoints} ratio={RatioMatches} inliers={Inliers} | coarse homography FAILED",
                coarse.KaKeypoints, coarse.KbKeypoints, coarse.RatioMatches, coarse.Inliers);
            throw new InvalidOperationException("Coarse homography failed (too few texture matches).");
        }

        double[,] h = coarse.H;

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

        // Warp-carry: predict a new-image position for EVERY left hold (matched ones too), aligned
        // 1:1 with leftHolds, so the carryover can reposition UNMATCHED old holds into the new image
        // instead of leaving them at stale coordinates. Purely an added output — gating is unaffected.
        var warpedLeft = WarpAllLeft(build.Field, cL, wr, hr);

        // Warp-carry (shapes): transform each left hold's custom outline onto the new image the same way
        // — every polygon vertex through the SAME warp field that warps centres — so a hand-drawn shape
        // (e.g. a triangular volume) lands correctly on the new photo. Aligned 1:1 with leftHolds; null
        // where the hold has no custom outline or the field cannot predict. Added output only; no gating.
        var warpedShapes = WarpAllShapes(build.Field, leftHolds, wl, hl, wr, hr);
        Pt[] predR = build.Field.Predict(cLb);
        Pt[] predL = rfield.Predict(cRb);

        // Nearest / second-nearest each direction → mutual NN + ambiguity margin.
        var nnR = predR.Select(p => MatchGeometry.KNearest(cRb, p, 2)).ToArray();
        var nnL = predL.Select(p => MatchGeometry.KNearest(cLb, p, 2)).ToArray();

        var (proposals, diags) = ScoreAndAssign(
            li, ri, descLb, descRb, qmap, predR, nnR, nnL, effL, effR,
            out List<double> residuals, out int gatedOut, out int colourGated, out int mutualNn);
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

        if (diag is not null)
        {
            int rescued = proposals.Count(p => p.Rescue is not null);
            diag.LogInformation(
                "[Matcher] homography ka={KaKeypoints} kb={KbKeypoints} ratio={RatioMatches} inliers={Inliers} | "
                + "band old={OldHolds} new={NewHolds} inbandL={InBandLeft} inbandR={InBandRight} | "
                + "warp tex={TextureAnchors} boot={BootAnchors} | "
                + "resid p50={ResidP50} p90={ResidP90} gatedOut={GatedOut} colourGated={ColourGated} | "
                + "match proposals={Proposals} mutual={MutualNn} rescued={Rescued} unmatchedL={UnmatchedLeft} unmatchedR={UnmatchedRight}",
                coarse.KaKeypoints, coarse.KbKeypoints, coarse.RatioMatches, coarse.Inliers,
                leftHolds.Count, rightHolds.Count, li.Count, ri.Count,
                build.TextureAnchors, build.BootAnchors,
                Percentile(residuals, 0.50), Percentile(residuals, 0.90), gatedOut, colourGated,
                proposals.Count, mutualNn, rescued, unmatchedL.Count, unmatchedR.Count);
        }

        return new HoldOverlapResult(outProposals, unmatchedL, unmatchedR, warpedLeft, warpedShapes);
    }

    /// <summary>
    /// Warp-predicts the new-image OUTLINE of every left hold that has a custom <see cref="MatcherHold.Shape"/>,
    /// returned as ABSOLUTE new-image normalized (0..1) vertices and aligned 1:1 with <paramref name="leftHolds"/>.
    /// Each old-image normalized vertex is taken to old pixels, pushed through the same local warp field that
    /// repositions centres, then normalized to the new image. An entry is null when the hold has no custom
    /// outline, the field has no anchors, or any vertex fit is non-finite (guarded), so callers fall back to
    /// the clone's copied (old) shape where a warped one is unavailable.
    /// </summary>
    private static IReadOnlyList<IReadOnlyList<(double X, double Y)>?> WarpAllShapes(
        LocalWarpField field, IReadOnlyList<MatcherHold> leftHolds,
        int oldWidth, int oldHeight, int newWidth, int newHeight)
    {
        var outp = new IReadOnlyList<(double X, double Y)>?[leftHolds.Count];
        if (field.AnchorCount == 0 || oldWidth <= 0 || oldHeight <= 0 || newWidth <= 0 || newHeight <= 0)
        {
            return outp;
        }

        for (int i = 0; i < leftHolds.Count; i++)
        {
            IReadOnlyList<(double X, double Y)>? shape = leftHolds[i].Shape;
            if (shape is null || shape.Count < 3)
            {
                continue;
            }

            var warped = new (double X, double Y)[shape.Count];
            bool ok = true;
            for (int v = 0; v < shape.Count; v++)
            {
                Pt p = field.Predict(new Pt(shape[v].X * oldWidth, shape[v].Y * oldHeight));
                if (!double.IsFinite(p.X) || !double.IsFinite(p.Y))
                {
                    ok = false;
                    break;
                }

                warped[v] = (p.X / newWidth, p.Y / newHeight);
            }

            if (ok)
            {
                outp[i] = warped;
            }
        }

        return outp;
    }

    /// <summary>
    /// Warp-predicts a new-image (right) position for every left-hold centre, returned in new-image
    /// NORMALIZED (0..1) coordinates and aligned 1:1 with <paramref name="leftCentresPx"/>. An entry
    /// is null when the field has no anchors or the local fit is non-finite (guarded), so callers get
    /// a stable-length list and simply fall back to the old position where prediction is unavailable.
    /// </summary>
    private static IReadOnlyList<(double X, double Y)?> WarpAllLeft(
        LocalWarpField field, Pt[] leftCentresPx, int newWidth, int newHeight)
    {
        var outp = new (double X, double Y)?[leftCentresPx.Length];
        if (field.AnchorCount == 0 || newWidth <= 0 || newHeight <= 0)
        {
            return outp;
        }

        for (int i = 0; i < leftCentresPx.Length; i++)
        {
            Pt p = field.Predict(leftCentresPx[i]);
            if (double.IsFinite(p.X) && double.IsFinite(p.Y))
            {
                outp[i] = (p.X / newWidth, p.Y / newHeight);
            }
        }

        return outp;
    }

    /// <summary>Rounded percentile of a residual sample; 0 when the sample is empty. Diagnostics only.</summary>
    private static int Percentile(List<double> values, double q)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        var sorted = values.ToArray();
        Array.Sort(sorted);
        int idx = (int)Math.Ceiling(q * (sorted.Length - 1));
        if (idx < 0)
        {
            idx = 0;
        }

        if (idx >= sorted.Length)
        {
            idx = sorted.Length - 1;
        }

        return (int)Math.Round(sorted[idx]);
    }

    private static (List<Proposal> Proposals, List<MatchDiag> Diags) ScoreAndAssign(
        List<int> li, List<int> ri,
        List<float[]?> descLb, List<float[]?> descRb, ColorQuantileMap qmap,
        Pt[] predR, (int[] Idx, double[] Dist)[] nnR, (int[] Idx, double[] Dist)[] nnL,
        double[]?[] effL, double[]?[] effR,
        out List<double> residuals, out int gatedOut, out int colourGated, out int mutualNn)
    {
        // Diagnostics accumulators: residuals of every candidate that cleared the gate, and the
        // counts of candidates rejected by the residual gate and by the colour sanity-gate.
        residuals = new List<double>();
        gatedOut = 0;
        colourGated = 0;

        var cand = new List<(double Conf, int A, int B, double Resid, double Dcol, double App, bool Mutual)>();
        for (int a = 0; a < predR.Length; a++)
        {
            int b = nnR[a].Idx[0];
            double resid = nnR[a].Dist[0];
            if (resid >= GatePx)
            {
                gatedOut++;
                continue;
            }

            residuals.Add(resid);
            double d2 = nnR[a].Dist.Length > 1 ? nnR[a].Dist[1] : resid;
            double margin = Math.Max(0.0, (d2 - resid) / (d2 + 1e-6));
            bool mutual = nnL[b].Idx[0] == a && nnL[b].Dist[0] < GatePx;

            int i = li[a], j = ri[b];
            double appearance = WarpFieldBuilder.Ncc(descLb[a], descRb[b]);
            double dcol = LabMath.Distance(effL[i], effR[j], qmap);

            // Colour sanity-gate (primary assignment only): a candidate whose old↔new quantile-mapped
            // Lab colour differs too much is a mispair (e.g. an old marker on bare wood paired to a
            // blue hold) — reject it so it is neither assigned nor allowed to steal the correct match.
            // The rejected old hold falls through to warp-carry and the new hold stays available for
            // its true old hold. Skipped when either colour is missing (never reject on unknown).
            // The colour-rescue pass deliberately re-uses colour to ADD matches and is not gated here.
            if (ColorGate.Rejects(effL[i], effR[j], dcol))
            {
                colourGated++;
                continue;
            }

            double geo = Math.Exp(-Math.Pow(resid / GeoScale, 2));
            double col = Math.Max(0.0, 1 - (dcol / 45.0));
            double app = Math.Max(0.0, appearance);
            double conf = (0.30 * geo) + (0.27 * margin) + (0.18 * (mutual ? 1.0 : 0.0))
                          + (0.15 * app) + (0.10 * col);
            cand.Add((conf, a, b, resid, dcol, appearance, mutual));
        }

        cand.Sort((x, y) => y.Conf.CompareTo(x.Conf));
        var usedL = new HashSet<int>();
        var usedR = new HashSet<int>();
        var proposals = new List<Proposal>();
        var diags = new List<MatchDiag>();
        mutualNn = 0;
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
            if (c.Mutual)
            {
                mutualNn++;
            }
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
