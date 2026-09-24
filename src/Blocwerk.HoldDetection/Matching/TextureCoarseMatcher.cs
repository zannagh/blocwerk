using OpenCvSharp;

namespace Blocwerk.HoldDetection.Matching;

/// <summary>
/// The coarse stage of photo → texture matching: a whole-image homography (<see cref="HomographyHelper.Coarse"/>,
/// AKAZE + RANSAC) on downscaled copies, tried at two scale pairs and the better kept. A photo taken from 2-3 m
/// resolves the wall at ~1-1.5 mm/px at 2400 px; a texture brought to 4 mm/px then shows it 3× smaller, which
/// AKAZE only partly absorbs. Measured on The Attic (right panel photo × main wall): 25 coarse inliers at
/// 1600 px / 4 mm/px, 89 at 2400 px / 2 mm/px. The small pass stays for photos taken from far away.
/// </summary>
internal static class TextureCoarseMatcher
{
    /// <summary>Finest photo resolution a coarse fit may imply at the photo centre (a close-up), mm/px.</summary>
    internal const double MinPhotoMmPerPx = 0.1;

    /// <summary>Coarsest photo resolution a coarse fit may imply at the photo centre, mm/px.</summary>
    internal const double MaxPhotoMmPerPx = 15;

    /// <summary>Longest side of the texture in the coarse stage.</summary>
    internal const int MaxTextureSide = 3600;

    /// <summary>The (photo side px, texture mm/px) pairs tried.</summary>
    internal static readonly (int PhotoSide, double TextureMmPerPx)[] Passes = [(1600, 4), (2400, 2)];

    /// <summary>Finds the coarse photo px → texture px homography (full resolution both), or null.</summary>
    /// <param name="photo">The photo, 8-bit gray, full resolution.</param>
    /// <param name="texture">The texture, 8-bit gray, full resolution.</param>
    /// <param name="textureMmPerPx">The texture's resolution.</param>
    /// <returns>The best pass: its homography (null when none held), inliers and ratio matches.</returns>
    public static (double[,]? H, int Inliers, int RatioMatches) Find(Mat photo, Mat texture, double textureMmPerPx)
    {
        (double[,]? H, int Inliers, int RatioMatches) best = (null, 0, 0);
        foreach (var (side, mm) in Passes)
        {
            var pass = Pass(photo, texture, textureMmPerPx, side, mm);
            if ((pass.H is not null && (best.H is null || pass.Inliers > best.Inliers))
                || (best.H is null && pass.H is null && pass.Inliers > best.Inliers))
            {
                best = pass;
            }
        }

        return best;
    }

    /// <summary>
    /// Whether a coarse fit is a view at all: somewhere on a grid over the photo that lands on the texture, the
    /// fit keeps orientation and implies a photo scale between <see cref="MinPhotoMmPerPx"/> and
    /// <see cref="MaxPhotoMmPerPx"/>. A handful of chance inliers on a small texture (The Attic's kickboards)
    /// passed the inlier bar with the whole photo collapsed onto a few texture pixels. Tested over the photo, not
    /// at its centre, since a side facet seen at a grazing angle fills only one edge of it.
    /// </summary>
    internal static bool Plausible(double[,] h, Size photo, Size texture, double textureMmPerPx)
    {
        const int n = 8;
        for (var i = 0; i < n * n; i++)
        {
            var x = ((i % n) + 0.5) * photo.Width / n;
            var y = ((i / n) + 0.5) * photo.Height / n;
            var w = Mat3.Depth(h, x, y);
            if (Math.Abs(w) < 1e-12)
            {
                continue;
            }

            var u = ((h[0, 0] * x) + (h[0, 1] * y) + h[0, 2]) / w;
            var v = ((h[1, 0] * x) + (h[1, 1] * y) + h[1, 2]) / w;
            var det = (((h[0, 0] - (u * h[2, 0])) * (h[1, 1] - (v * h[2, 1]))) - ((h[0, 1] - (u * h[2, 1])) * (h[1, 0] - (v * h[2, 0])))) / (w * w);
            var mmPerPx = Math.Sqrt(Math.Max(0, det)) * textureMmPerPx;
            var onTexture = u >= 0 && u < texture.Width && v >= 0 && v < texture.Height;
            if (onTexture && det > 0 && mmPerPx >= MinPhotoMmPerPx && mmPerPx <= MaxPhotoMmPerPx)
            {
                return true;
            }
        }

        return false;
    }

    private static (double[,]? H, int Inliers, int RatioMatches) Pass(
        Mat photo, Mat texture, double textureMmPerPx, int photoSide, double coarseMmPerPx)
    {
        var sp = Math.Min(1, photoSide / (double)Math.Max(photo.Width, photo.Height));
        var st = Math.Min(1, Math.Min(textureMmPerPx / coarseMmPerPx, MaxTextureSide / (double)Math.Max(texture.Width, texture.Height)));
        using var coarsePhoto = ToBgr(photo, sp);
        using var coarseTexture = ToBgr(texture, st);
        var (hc, _, _, ratioMatches, inliers) = HomographyHelper.Coarse(coarsePhoto, coarseTexture, null, 1.0);
        if (hc is null)
        {
            return (null, inliers, ratioMatches);
        }

        var stx = (double)coarseTexture.Width / texture.Width;
        var sty = (double)coarseTexture.Height / texture.Height;
        var spx = (double)coarsePhoto.Width / photo.Width;
        var spy = (double)coarsePhoto.Height / photo.Height;
        var h = Mat3.Mul(Mat3.Invert(Mat3.Scale(stx, sty)), Mat3.Mul(hc, Mat3.Scale(spx, spy)));
        return Plausible(h, photo.Size(), texture.Size(), textureMmPerPx) ? (h, inliers, ratioMatches) : (null, inliers, ratioMatches);
    }

    /// <summary>A downscaled BGR copy, the form <see cref="HomographyHelper.Coarse"/> takes.</summary>
    private static Mat ToBgr(Mat gray, double s)
    {
        using var small = TextureFineMatcher.Resized(gray, s, InterpolationFlags.Area);
        var bgr = new Mat();
        Cv2.CvtColor(small, bgr, ColorConversionCodes.GRAY2BGR);
        return bgr;
    }
}
