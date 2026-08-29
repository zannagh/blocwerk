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
    /// <summary>Estimates the coarse L→R homography as a 3x3 matrix, with the RANSAC inlier count.</summary>
    /// <returns>(H, inliers) or (null, 0) when too few matches were found.</returns>
    public static (double[,]? H, int Inliers) Coarse(
        Mat imgL, Mat imgR, double s = 0.35, double ratio = 0.75)
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
            return (null, 0);
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

        if (src.Count < 8)
        {
            return (null, 0);
        }

        using var mask = new Mat();
        using Mat h = Cv2.FindHomography(src, dst, HomographyMethods.Ransac, 5.0, mask);
        if (h.Empty())
        {
            return (null, 0);
        }

        int inliers = Cv2.CountNonZero(mask);
        return (ToArray(h), inliers);
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

    private static double[,] ToArray(Mat m)
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
