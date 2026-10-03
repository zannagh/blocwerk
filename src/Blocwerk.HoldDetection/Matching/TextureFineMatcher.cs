using Blocwerk.Core.Geometry;
using OpenCvSharp;

namespace Blocwerk.HoldDetection.Matching;

/// <summary>
/// The fine stage of photo → texture matching: the photo is warped into the texture's grid through the coarse
/// homography, so both images show the facet at the same scale and orientation, and features are matched
/// again, guided (<see cref="GuidedDescriptorMatcher"/>). The pairs come back in full-resolution photo and
/// texture pixels; a warped point maps back to the photo exactly through the inverse of the warp.
/// </summary>
internal static class TextureFineMatcher
{
    /// <summary>Texture resolution the fine stage works at (it never upsamples).</summary>
    internal const double FineMmPerPx = 2;

    /// <summary>Largest fine-grid side, so a huge facet stays a few megapixels.</summary>
    internal const int MaxFineSide = 4000;

    /// <summary>How far a feature's partner may be from where the coarse homography puts it.</summary>
    internal const double SearchRadiusMm = 40;

    /// <summary>Strongest keypoints kept per image.</summary>
    internal const int MaxKeypoints = 25_000;

    private const int MaskErodePx = 6;

    /// <summary>Matches a photo against a texture it roughly maps onto.</summary>
    /// <param name="photo">The photo, 8-bit gray, full resolution.</param>
    /// <param name="texture">The texture, 8-bit gray, full resolution.</param>
    /// <param name="mask">The texture's coverage mask (same size, 0 = uncovered), or null.</param>
    /// <param name="h">Coarse homography, photo px → texture px.</param>
    /// <param name="textureMmPerPx">The texture's resolution.</param>
    /// <param name="searchRadiusMm">How far from its predicted position a partner is searched, mm.</param>
    /// <returns>The pairs, photo px → texture px.</returns>
    public static List<PointCorrespondence> Match(
        Mat photo, Mat texture, Mat? mask, double[,] h, double textureMmPerPx, double searchRadiusMm = SearchRadiusMm)
    {
        var s = Math.Min(1, Math.Min(textureMmPerPx / FineMmPerPx, MaxFineSide / (double)Math.Max(texture.Width, texture.Height)));
        using var fineTexture = Resized(texture, s, InterpolationFlags.Area);
        var (sx, sy) = ((double)fineTexture.Width / texture.Width, (double)fineTexture.Height / texture.Height);
        using var fineMask = FineMask(mask, fineTexture.Size());
        var toFine = Mat3.Mul(Mat3.Scale(sx, sy), h);
        if (Roi(photo.Size(), toFine, fineTexture.Size()) is not { } roi)
        {
            return [];
        }

        var toRoi = Mat3.Mul(Mat3.Translate(-roi.X, -roi.Y), toFine);
        using var warped = Warp(photo, toRoi, roi.Size, InterpolationFlags.Linear);
        using var both = ValidMask(photo.Size(), toRoi, roi, fineMask);
        using var textureRoi = new Mat(fineTexture, roi);
        var (kw, dw) = Detect(warped, both);
        var (kt, dt) = Detect(textureRoi, both);
        using (dw)
        using (dt)
        {
            var radius = Math.Max(4, searchRadiusMm * sx / textureMmPerPx);
            var back = HomographyHelper.Invert(toRoi);
            var toTexture = Mat3.Invert(Mat3.Mul(Mat3.Translate(-roi.X, -roi.Y), Mat3.Scale(sx, sy)));
            return GuidedDescriptorMatcher.Match(kw, dw, kt, dt, radius)
                .Select(m => Pair(back, toTexture, kw[m.Query].Pt, kt[m.Train].Pt))
                .ToList();
        }
    }

    /// <summary>A resized copy (a plain copy at scale 1).</summary>
    /// <param name="src">The image.</param>
    /// <param name="s">The scale.</param>
    /// <param name="flags">Interpolation.</param>
    /// <returns>The copy.</returns>
    internal static Mat Resized(Mat src, double s, InterpolationFlags flags)
    {
        var dst = new Mat();
        if (s >= 1)
        {
            src.CopyTo(dst);
            return dst;
        }

        var size = new Size(Math.Max(1, (int)Math.Round(src.Width * s)), Math.Max(1, (int)Math.Round(src.Height * s)));
        Cv2.Resize(src, dst, size, 0, 0, flags);
        return dst;
    }

    private static PointCorrespondence Pair(double[,] back, double[,] toTexture, Point2f warped, Point2f texture)
    {
        var p = HomographyHelper.Warp(back, new Pt(warped.X, warped.Y));
        var t = HomographyHelper.Warp(toTexture, new Pt(texture.X, texture.Y));
        return new PointCorrespondence(p.X, p.Y, t.X, t.Y);
    }

    /// <summary>Where both images carry the facet: inside the warped photo and inside the texture's coverage, eroded.</summary>
    private static Mat ValidMask(Size photo, double[,] toRoi, Rect roi, Mat fineMask)
    {
        using var ones = new Mat(photo, MatType.CV_8UC1, Scalar.All(255));
        using var valid = Warp(ones, toRoi, roi.Size, InterpolationFlags.Nearest);
        using var coverage = new Mat(fineMask, roi);
        var both = new Mat();
        Cv2.BitwiseAnd(valid, coverage, both);
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size((2 * MaskErodePx) + 1, (2 * MaskErodePx) + 1));
        Cv2.Erode(both, both, kernel);
        return both;
    }

    /// <summary>
    /// The part of the fine grid the photo covers (its corners' bounds, clipped), or the whole grid when a
    /// corner lies beyond the homography's horizon. Null when the photo covers too little of it.
    /// </summary>
    private static Rect? Roi(Size photo, double[,] toFine, Size grid)
    {
        (double X, double Y)[] corners = [(0, 0), (photo.Width, 0), (photo.Width, photo.Height), (0, photo.Height)];
        var sign = Math.Sign(Mat3.Depth(toFine, photo.Width / 2.0, photo.Height / 2.0));
        if (corners.Any(c => Math.Sign(Mat3.Depth(toFine, c.X, c.Y)) != sign))
        {
            return new Rect(0, 0, grid.Width, grid.Height);
        }

        var mapped = corners.Select(c => HomographyHelper.Warp(toFine, new Pt(c.X, c.Y))).ToList();
        var x0 = Math.Max(0, (int)Math.Floor(mapped.Min(p => p.X)));
        var y0 = Math.Max(0, (int)Math.Floor(mapped.Min(p => p.Y)));
        var x1 = Math.Min(grid.Width, (int)Math.Ceiling(mapped.Max(p => p.X)) + 1);
        var y1 = Math.Min(grid.Height, (int)Math.Ceiling(mapped.Max(p => p.Y)) + 1);
        return x1 - x0 < 32 || y1 - y0 < 32 ? null : new Rect(x0, y0, x1 - x0, y1 - y0);
    }

    private static (KeyPoint[] Keypoints, Mat Descriptors) Detect(Mat gray, Mat mask)
    {
        using var akaze = AKAZE.Create(threshold: 0.0005f);
        using var equalised = new Mat();
        using (var clahe = Cv2.CreateCLAHE(2.0, new Size(8, 8)))
        {
            clahe.Apply(gray, equalised);
        }

        var keypoints = KeyPointsFilter.RetainBest(akaze.Detect(equalised, mask), MaxKeypoints);
        var descriptors = new Mat();
        if (keypoints.Length > 0)
        {
            akaze.Compute(equalised, ref keypoints, descriptors);
        }

        return (keypoints, descriptors);
    }

    private static Mat FineMask(Mat? mask, Size size)
    {
        if (mask is null)
        {
            return new Mat(size, MatType.CV_8UC1, Scalar.All(255));
        }

        var fine = new Mat();
        Cv2.Resize(mask, fine, size, 0, 0, InterpolationFlags.Nearest);
        Cv2.Threshold(fine, fine, 127, 255, ThresholdTypes.Binary);
        return fine;
    }

    private static Mat Warp(Mat src, double[,] h, Size size, InterpolationFlags flags)
    {
        using var m = Mat3.ToMat(h);
        var dst = new Mat();
        Cv2.WarpPerspective(src, dst, m, size, flags, BorderTypes.Constant, Scalar.All(0));
        return dst;
    }
}
