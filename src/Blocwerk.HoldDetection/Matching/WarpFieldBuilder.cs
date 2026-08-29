using OpenCvSharp;

namespace Blocwerk.HoldDetection.Matching;

/// <summary>
/// Builds the local warp field from two anchor sources: texture correspondences on the
/// wall (AKAZE — the OpenCvSharp base package has no SIFT — filtered by local displacement
/// consistency so parallax survives) and bootstrapped high-confidence hold-centre matches
/// that add anchors exactly where holds are, including the sparse top/bottom. Faithful
/// port of the Python <c>warpfield.build_field</c>.
/// </summary>
internal static class WarpFieldBuilder
{
    /// <summary>Result of a field build, including anchor counts and the raw anchors for the reverse field.</summary>
    public sealed record BuildResult(
        LocalWarpField Field,
        int TextureAnchors,
        int BootAnchors,
        IReadOnlyList<Pt> AnchorsSrc,
        IReadOnlyList<Pt> AnchorsDst);

    /// <summary>
    /// Texture correspondences (AKAZE + Hamming BF + Lowe ratio) at a downscale, mapped back
    /// to full-resolution pixel coordinates.
    /// </summary>
    public static (List<Pt> Src, List<Pt> Dst) TextureCorrespondences(
        Mat imgL, Mat imgR, double s = 0.6, double ratio = 0.8, float akazeThreshold = 0.0004f)
    {
        using var a = new Mat();
        using var b = new Mat();
        Cv2.Resize(imgL, a, default(Size), s, s, InterpolationFlags.Area);
        Cv2.Resize(imgR, b, default(Size), s, s, InterpolationFlags.Area);
        using var ga = new Mat();
        using var gb = new Mat();
        Cv2.CvtColor(a, ga, ColorConversionCodes.BGR2GRAY);
        Cv2.CvtColor(b, gb, ColorConversionCodes.BGR2GRAY);

        // AKAZE replaces the reference SIFT; a lower-than-default detector threshold recovers
        // a comparable anchor density (SIFT found ~2x more keypoints at default settings).
        using var akaze = AKAZE.Create(
            AKAZEDescriptorType.MLDB, 0, 3, akazeThreshold);
        using var da = new Mat();
        using var db = new Mat();
        akaze.DetectAndCompute(ga, null, out KeyPoint[] ka, da);
        akaze.DetectAndCompute(gb, null, out KeyPoint[] kb, db);

        var src = new List<Pt>();
        var dst = new List<Pt>();
        if (ka.Length == 0 || kb.Length == 0 || da.Rows == 0 || db.Rows == 0)
        {
            return (src, dst);
        }

        using var bf = new BFMatcher(NormTypes.Hamming);
        DMatch[][] knn = bf.KnnMatch(da, db, 2);
        foreach (DMatch[] pair in knn)
        {
            if (pair.Length < 2)
            {
                continue;
            }

            if (pair[0].Distance < ratio * pair[1].Distance)
            {
                Point2f pa = ka[pair[0].QueryIdx].Pt;
                Point2f pb = kb[pair[0].TrainIdx].Pt;
                src.Add(new Pt(pa.X / s, pa.Y / s));
                dst.Add(new Pt(pb.X / s, pb.Y / s));
            }
        }

        return (src, dst);
    }

    /// <summary>
    /// Keeps a correspondence only if its displacement agrees with the median displacement
    /// of its k nearest neighbours — removes gross mismatches without imposing a single
    /// global model (so parallax is preserved).
    /// </summary>
    public static bool[] LocalConsistencyFilter(
        IReadOnlyList<Pt> src, IReadOnlyList<Pt> dst, int k = 25, double tol = 40.0)
    {
        int n = src.Count;
        var keep = new bool[n];
        if (n < k)
        {
            Array.Fill(keep, true);
            return keep;
        }

        var disp = new Pt[n];
        for (int i = 0; i < n; i++)
        {
            disp[i] = new Pt(dst[i].X - src[i].X, dst[i].Y - src[i].Y);
        }

        for (int i = 0; i < n; i++)
        {
            var (idx, _) = MatchGeometry.KNearest(src, src[i], k);
            var xs = new double[idx.Length];
            var ys = new double[idx.Length];
            for (int j = 0; j < idx.Length; j++)
            {
                xs[j] = disp[idx[j]].X;
                ys[j] = disp[idx[j]].Y;
            }

            double mx = MedianOf(xs);
            double my = MedianOf(ys);
            double dx = disp[i].X - mx;
            double dy = disp[i].Y - my;
            keep[i] = Math.Sqrt((dx * dx) + (dy * dy)) < tol;
        }

        return keep;
    }

    /// <summary>
    /// Texture anchors + bootstrapped confident hold matches → <see cref="LocalWarpField"/>.
    /// </summary>
    public static BuildResult Build(
        Mat imgL, Mat imgR,
        IReadOnlyList<Pt> cLb, IReadOnlyList<Pt> cRb,
        IReadOnlyList<float[]?> descL, IReadOnlyList<float[]?> descR,
        int bootIters = 4, double bootGate = 28.0, double bootNcc = 0.35)
    {
        // Defaults are adapted from the reference (bootIters 2, gate 22, ncc 0.45): AKAZE's
        // initial texture field is coarser than SIFT's, so more bootstrap iterations and a
        // slightly looser gate/appearance threshold are needed to converge to the same field
        // accuracy. Validated on hand-labelled pair 2-3 (P=1.00, R=0.909 @conf≥0.45).
        var (texSrc, texDst) = TextureCorrespondences(imgL, imgR);
        bool[] keep = LocalConsistencyFilter(texSrc, texDst);
        var src = new List<Pt>();
        var dst = new List<Pt>();
        for (int i = 0; i < texSrc.Count; i++)
        {
            if (keep[i])
            {
                src.Add(texSrc[i]);
                dst.Add(texDst[i]);
            }
        }

        int nTexture = src.Count;
        var field = new LocalWarpField(src, dst);
        int nBoot = 0;
        List<Pt> allSrc = src;
        List<Pt> allDst = dst;

        for (int iter = 0; iter < bootIters; iter++)
        {
            Pt[] pred = field.Predict(cLb);
            var bootSrc = new List<Pt>();
            var bootDst = new List<Pt>();
            for (int a = 0; a < cLb.Count; a++)
            {
                var (idx, dis) = MatchGeometry.KNearest(cRb, pred[a], 1);
                if (dis[0] >= bootGate)
                {
                    continue;
                }

                int j = idx[0];
                double appearance = Ncc(descL[a], descR[j]);
                if (appearance < bootNcc)
                {
                    continue;
                }

                bootSrc.Add(cLb[a]);
                bootDst.Add(cRb[j]);
            }

            nBoot = bootSrc.Count;
            if (nBoot < 4)
            {
                break;
            }

            allSrc = new List<Pt>(src);
            allDst = new List<Pt>(dst);
            allSrc.AddRange(bootSrc);
            allDst.AddRange(bootDst);
            field = new LocalWarpField(allSrc, allDst);
        }

        return new BuildResult(field, nTexture, nBoot, allSrc, allDst);
    }

    /// <summary>Zero-mean unit-norm patch dot product (NCC). Returns 0 when either patch is missing.</summary>
    public static double Ncc(float[]? a, float[]? b)
    {
        if (a is null || b is null || a.Length != b.Length)
        {
            return 0.0;
        }

        double sum = 0.0;
        for (int i = 0; i < a.Length; i++)
        {
            sum += a[i] * b[i];
        }

        return sum;
    }

    private static double MedianOf(double[] values)
    {
        var copy = (double[])values.Clone();
        Array.Sort(copy);
        int n = copy.Length;
        if (n == 0)
        {
            return 0.0;
        }

        return n % 2 == 1 ? copy[n / 2] : 0.5 * (copy[(n / 2) - 1] + copy[n / 2]);
    }
}
