using Blocwerk.Core.Abstractions;
using Microsoft.Extensions.Logging;
using OpenCvSharp;

namespace Blocwerk.HoldDetection.Matching;

/// <summary>
/// Everything the matcher does differently when it receives a <see cref="HoldOverlapSeed"/>: reading the
/// images in the RAW frame, turning the normalized seed homography into pixels, and choosing between the
/// seed and the texture (AKAZE) coarse homography. Never used without a seed.
/// </summary>
internal static class SeededCoarse
{
    /// <summary>Seed and AKAZE "disagree wildly" above this fraction of the right image diagonal.</summary>
    internal const double DisagreementFraction = 0.05;

    /// <summary>
    /// Decodes an image for matching. With a seed the RAW pixel grid is used (EXIF orientation ignored):
    /// that is the frame hold X/Y and marker corners are normalized in. Without one the decode is exactly
    /// as it always was (OpenCV applies the orientation tag).
    /// </summary>
    public static Mat Decode(byte[] image, bool seeded) =>
        Cv2.ImDecode(image, seeded ? ImreadModes.Color | ImreadModes.IgnoreOrientation : ImreadModes.Color);

    /// <summary>The seed's normalized homography expressed in left-pixel → right-pixel coordinates.</summary>
    public static double[,] ToPixels(IReadOnlyList<double> hn, int wl, int hl, int wr, int hr)
    {
        // Hpx = S_R · Hn · S_L⁻¹ with S = diag(w, h, 1).
        var h = new double[3, 3];
        double[] rowScale = [wr, hr, 1];
        double[] colScale = [1.0 / wl, 1.0 / hl, 1];
        for (int r = 0; r < 3; r++)
        {
            for (int c = 0; c < 3; c++)
            {
                h[r, c] = hn[(r * 3) + c] * rowScale[r] * colScale[c];
            }
        }

        return h;
    }

    /// <summary>
    /// Picks the coarse homography for a seeded run. AKAZE still runs, as a fallback and a sanity check:
    /// when it fails the seed is used whatever its quality. Otherwise only a MULTI-marker seed may replace
    /// it — a single-marker seed is exact at the marker and degrades fast away from it (measured: it made
    /// matching worse than AKAZE on real pairs). A disagreement above <see cref="DisagreementFraction"/> of
    /// the image diagonal is logged either way. <c>usedSeed</c> reports whether the seed was chosen.
    /// </summary>
    public static (double[,]? H, int KaKeypoints, int KbKeypoints, int RatioMatches, int Inliers) Resolve(
        Mat imgL, Mat imgR, HoldOverlapSeed seed, Pt[] leftCentres, ILogger? diag, out bool usedSeed)
    {
        var akaze = HomographyHelper.Coarse(imgL, imgR, () => WarpFieldBuilder.CountTextureAnchors(imgL, imgR));
        usedSeed = false;
        if (seed.Homography is null)
        {
            return akaze;
        }

        var hs = ToPixels(seed.Homography, imgL.Width, imgL.Height, imgR.Width, imgR.Height);
        if (akaze.H is null)
        {
            diag?.LogInformation("[Matcher] seed {Source} used: texture homography failed", seed.Source);
            usedSeed = true;
            return (hs, akaze.KaKeypoints, akaze.KbKeypoints, akaze.RatioMatches, akaze.Inliers);
        }

        double disagreement = MedianDisagreement(hs, akaze.H, leftCentres, imgL.Width, imgL.Height);
        double limit = DisagreementFraction * Math.Sqrt((imgR.Width * (double)imgR.Width) + (imgR.Height * (double)imgR.Height));
        bool useSeed = seed.IsMultiMarker;
        usedSeed = useSeed;
        if (disagreement > limit)
        {
            diag?.LogWarning(
                "[Matcher] seed {Source} ({Markers} markers) and texture homography disagree by {Px:F0}px (limit {Limit:F0}); using {Choice}",
                seed.Source, seed.MarkerCount, disagreement, limit, useSeed ? "seed" : "texture");
        }

        return (useSeed ? hs : akaze.H, akaze.KaKeypoints, akaze.KbKeypoints, akaze.RatioMatches, akaze.Inliers);
    }

    /// <summary>Median distance between the two mappings over the left holds (an image grid when there are none).</summary>
    internal static double MedianDisagreement(double[,] a, double[,] b, Pt[] leftCentres, int wl, int hl)
    {
        var pts = leftCentres.Length > 0 ? leftCentres : Grid(wl, hl);
        var d = pts
            .Select(p => HomographyHelper.Warp(a, p).Dist(HomographyHelper.Warp(b, p)))
            .Where(double.IsFinite)
            .OrderBy(v => v)
            .ToArray();
        return d.Length == 0 ? double.PositiveInfinity : d[d.Length / 2];
    }

    private static Pt[] Grid(int w, int h)
    {
        var pts = new List<Pt>();
        for (int i = 1; i < 10; i++)
        {
            for (int j = 1; j < 10; j++)
            {
                pts.Add(new Pt(w * i / 10.0, h * j / 10.0));
            }
        }

        return pts.ToArray();
    }
}
