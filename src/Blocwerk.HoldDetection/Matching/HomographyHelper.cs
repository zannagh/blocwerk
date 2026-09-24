using OpenCvSharp;

namespace Blocwerk.HoldDetection.Matching;

/// <summary>
/// Coarse whole-image homography (AKAZE + RANSAC) used ONLY to locate the overlap band —
/// never as final geometry. On a non-planar wall a single homography is off by ~100px, so
/// the actual correspondence geometry comes from the local warp field. Port of the Python
/// <c>coarse_homography</c> with SIFT swapped for AKAZE (base-package constraint).
/// </summary>
internal static class HomographyHelper
{
    /// <summary>
    /// Fewest RANSAC inliers a coarse homography needs to be trusted. Measured: unrelated/barely-overlapping
    /// photos give 4-12 (garbage), while a real reframed same-panel update (The Attic right panel, 24 mm) gave only
    /// 16 yet carried ~95% of its holds correctly via the warp field. Keep this between those. A pair below it
    /// can still pass on wider texture evidence — see <see cref="MinRescueInliers"/>.
    /// </summary>
    internal const int MinInliers = 15;

    /// <summary>
    /// Below <see cref="MinInliers"/> a homography is still accepted when BOTH wider signals clear their bar
    /// (and it has at least this many inliers, so it is more than a minimal 4-point fit). Measured 2026-09-23:
    /// garbage pairs gave ratio matches 20-44 and texture anchors 1-77; the real reframed right panel 186 / 951,
    /// the weakest good pair 122 / 228. The bars sit ~2.3x / 2.6x above garbage; no measured outcome changes.
    /// </summary>
    internal const int MinRescueInliers = 8;

    /// <summary>Ratio-test matches a sub-<see cref="MinInliers"/> homography needs to be rescued.</summary>
    internal const int MinRescueRatioMatches = 100;

    /// <summary>Locally consistent texture anchors a sub-<see cref="MinInliers"/> homography needs to be rescued.</summary>
    internal const int MinRescueTextureAnchors = 200;

    /// <summary>Estimates the coarse L→R homography as a 3x3 matrix, with diagnostic counts.</summary>
    /// <param name="imgL">Left image.</param>
    /// <param name="imgR">Right image.</param>
    /// <param name="textureAnchors">
    /// Counts the pair's locally consistent texture anchors; asked only for a borderline homography (see
    /// <see cref="MinRescueInliers"/>), since it costs a second, denser AKAZE pass. Null disables the rescue.
    /// </param>
    /// <param name="s">Downscale for the coarse AKAZE pass.</param>
    /// <param name="ratio">Lowe ratio-test threshold.</param>
    /// <returns>
    /// (H, keypoint counts, ratio-match count, RANSAC inlier count). H is null when too few
    /// matches were found; the counts are still populated as far as the run got, so a failed
    /// run stays diagnosable.
    /// </returns>
    public static (double[,]? H, int KaKeypoints, int KbKeypoints, int RatioMatches, int Inliers) Coarse(
        Mat imgL, Mat imgR, Func<int>? textureAnchors = null, double s = 0.35, double ratio = 0.75)
    {
        using var a = new Mat();
        using var b = new Mat();
        Cv2.Resize(imgL, a, default(Size), s, s, InterpolationFlags.Area);
        Cv2.Resize(imgR, b, default(Size), s, s, InterpolationFlags.Area);
        using var ga = new Mat();
        using var gb = new Mat();
        Cv2.CvtColor(a, ga, ColorConversionCodes.BGR2GRAY);
        Cv2.CvtColor(b, gb, ColorConversionCodes.BGR2GRAY);

        using var akaze = AKAZE.Create();
        using var da = new Mat();
        using var db = new Mat();
        akaze.DetectAndCompute(ga, null, out KeyPoint[] ka, da);
        akaze.DetectAndCompute(gb, null, out KeyPoint[] kb, db);
        if (ka.Length == 0 || kb.Length == 0 || da.Rows == 0 || db.Rows == 0)
        {
            return (null, ka.Length, kb.Length, 0, 0);
        }

        using var bf = new BFMatcher(NormTypes.Hamming);
        DMatch[][] knn = bf.KnnMatch(da, db, 2);
        var src = new List<Point2d>();
        var dst = new List<Point2d>();
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
                src.Add(new Point2d(pa.X / s, pa.Y / s));
                dst.Add(new Point2d(pb.X / s, pb.Y / s));
            }
        }

        int ratioMatches = src.Count;
        if (src.Count < 8)
        {
            return (null, ka.Length, kb.Length, ratioMatches, 0);
        }

        using var mask = new Mat();
        using Mat h = Cv2.FindHomography(src, dst, HomographyMethods.Ransac, 5.0, mask);
        if (h.Empty())
        {
            return (null, ka.Length, kb.Length, ratioMatches, 0);
        }

        // A handful of RANSAC inliers is no consensus at all (any 4 points fit a homography): two photos
        // that barely share texture would otherwise yield an arbitrary H, and everything downstream — band,
        // warp field, proposals — would be confidently built on it. Treat it as a failed estimate.
        int inliers = Cv2.CountNonZero(mask);
        if (inliers < MinInliers && !Rescued(inliers, ratioMatches, textureAnchors))
        {
            return (null, ka.Length, kb.Length, ratioMatches, inliers);
        }

        return (ToArray(h), ka.Length, kb.Length, ratioMatches, inliers);
    }

    /// <summary>Applies a 3x3 homography to a pixel point.</summary>
    public static Pt Warp(double[,] h, Pt p)
    {
        double denom = (h[2, 0] * p.X) + (h[2, 1] * p.Y) + h[2, 2];
        if (Math.Abs(denom) < 1e-12)
        {
            denom = denom < 0 ? -1e-12 : 1e-12;
        }

        double x = ((h[0, 0] * p.X) + (h[0, 1] * p.Y) + h[0, 2]) / denom;
        double y = ((h[1, 0] * p.X) + (h[1, 1] * p.Y) + h[1, 2]) / denom;
        return new Pt(x, y);
    }

    /// <summary>Inverts a 3x3 homography, returning the inverse as a plain array.</summary>
    public static double[,] Invert(double[,] h)
    {
        using var m = new Mat(3, 3, MatType.CV_64FC1);
        for (int r = 0; r < 3; r++)
        {
            for (int c = 0; c < 3; c++)
            {
                m.Set(r, c, h[r, c]);
            }
        }

        using Mat inv = m.Inv();
        return ToArray(inv);
    }

    /// <summary>
    /// Whether a homography below <see cref="MinInliers"/> is still trusted: enough inliers to be more than a
    /// minimal fit, AND wide texture agreement on both the coarse ratio test and the dense anchor pass.
    /// </summary>
    internal static bool Rescued(int inliers, int ratioMatches, Func<int>? textureAnchors) =>
        textureAnchors is not null
        && inliers >= MinRescueInliers
        && ratioMatches >= MinRescueRatioMatches
        && textureAnchors() >= MinRescueTextureAnchors;

    internal static double[,] ToArray(Mat m)
    {
        var arr = new double[3, 3];
        for (int r = 0; r < 3; r++)
        {
            for (int c = 0; c < 3; c++)
            {
                arr[r, c] = m.At<double>(r, c);
            }
        }

        return arr;
    }
}
