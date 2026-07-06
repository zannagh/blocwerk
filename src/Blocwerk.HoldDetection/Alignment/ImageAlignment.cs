using Blocwerk.Core.Abstractions;

namespace Blocwerk.HoldDetection.Alignment;

/// <summary>
/// Estimates a homography between two photos entirely on-device using a
/// FAST + oriented-BRIEF + RANSAC pipeline. Robust enough to seed a manual
/// alignment; not intended to be pixel-perfect on its own.
/// </summary>
public static class ImageAlignment
{
    private const double LoweRatio = 0.8;
    private const int MinInliers = 15;
    private const double MinConfidence = 0.25;

    /// <summary>
    /// Estimates H mapping <paramref name="imageToAlign"/> pixels into
    /// <paramref name="baseImage"/>'s pixel frame. Returns null on failure.
    /// </summary>
    public static Homography? Estimate(byte[] baseImage, byte[] imageToAlign)
    {
        var core = EstimateCore(baseImage, imageToAlign);
        if (core == null)
        {
            return null;
        }

        var full = UnscaleToFullResolution(core.Value.H, core.Value.A.Scale, core.Value.B.Scale);
        return new Homography(full, core.Value.Inliers, Math.Round(core.Value.Confidence, 3));
    }

    /// <summary>
    /// Like <see cref="Estimate"/> but the returned homography maps normalized
    /// [0,1] coordinates of <paramref name="imageToAlign"/> to normalized [0,1]
    /// coordinates of <paramref name="baseImage"/>, so callers that store
    /// resolution-independent points (e.g. holds) need no image dimensions.
    /// </summary>
    public static Homography? EstimateNormalized(byte[] baseImage, byte[] imageToAlign)
    {
        var core = EstimateCore(baseImage, imageToAlign);
        if (core == null)
        {
            return null;
        }

        var pixels = UnscaleToFullResolution(core.Value.H, core.Value.A.Scale, core.Value.B.Scale);

        // H_norm = diag(1/baseW, 1/baseH) * H_px * diag(alignW, alignH)
        var toPx = new[] { (double)core.Value.B.OriginalWidth, 0, 0, 0, (double)core.Value.B.OriginalHeight, 0, 0, 0, 1.0 };
        var toNorm = new[] { 1.0 / core.Value.A.OriginalWidth, 0, 0, 0, 1.0 / core.Value.A.OriginalHeight, 0, 0, 0, 1.0 };
        var norm = LinearAlgebra.Mat3Mul(LinearAlgebra.Mat3Mul(toNorm, pixels), toPx);
        if (Math.Abs(norm[8]) > 1e-12)
        {
            for (var i = 0; i < 9; i++)
            {
                norm[i] /= norm[8];
            }
        }

        return new Homography(norm, core.Value.Inliers, Math.Round(core.Value.Confidence, 3));
    }

    private readonly record struct Core(double[] H, int Inliers, double Confidence, GrayImage A, GrayImage B);

    private static Core? EstimateCore(byte[] baseImage, byte[] imageToAlign)
    {
        var a = GrayImage.DecodeDownscaled(baseImage);
        var b = GrayImage.DecodeDownscaled(imageToAlign);
        if (a == null || b == null)
        {
            return null;
        }

        var kpA = FastDetector.Detect(a);
        var kpB = FastDetector.Detect(b);
        if (kpA.Count < 8 || kpB.Count < 8)
        {
            return null;
        }

        var descA = BriefDescriptor.Compute(a.Blurred(), kpA);
        var descB = BriefDescriptor.Compute(b.Blurred(), kpB);

        var matches = MatchMutual(descA, kpA, descB, kpB);
        if (matches.Count < 4)
        {
            return null;
        }

        var h = HomographyRansac.Estimate(matches, out var inliers);
        if (h == null)
        {
            return null;
        }

        var confidence = (double)inliers / matches.Count;
        if (inliers < MinInliers || confidence < MinConfidence)
        {
            return null;
        }

        return new Core(h, inliers, confidence, a, b);
    }

    /// <summary>
    /// Cross-checked matching with Lowe ratio test. Correspondences are stored as
    /// src = B keypoint, dst = A keypoint (so the homography maps B -> A).
    /// </summary>
    private static List<Correspondence> MatchMutual(
        ulong[] descA, List<Keypoint> kpA, ulong[] descB, List<Keypoint> kpB)
    {
        var bestBforA = BestMatch(descA, kpA.Count, descB, kpB.Count);
        var bestAforB = BestMatch(descB, kpB.Count, descA, kpA.Count);

        var matches = new List<Correspondence>();
        for (var b = 0; b < kpB.Count; b++)
        {
            var a = bestAforB[b].Index;
            if (a < 0 || !bestAforB[b].RatioOk)
            {
                continue;
            }

            // Mutual best check.
            if (bestBforA[a].Index != b)
            {
                continue;
            }

            matches.Add(new Correspondence(kpB[b].X, kpB[b].Y, kpA[a].X, kpA[a].Y));
        }

        return matches;
    }

    private static (int Index, bool RatioOk)[] BestMatch(
        ulong[] query, int queryCount, ulong[] train, int trainCount)
    {
        var result = new (int, bool)[queryCount];
        for (var q = 0; q < queryCount; q++)
        {
            var best = int.MaxValue;
            var second = int.MaxValue;
            var bestIdx = -1;
            for (var t = 0; t < trainCount; t++)
            {
                var d = BriefDescriptor.Hamming(query, q, train, t);
                if (d < best)
                {
                    second = best;
                    best = d;
                    bestIdx = t;
                }
                else if (d < second)
                {
                    second = d;
                }
            }

            var ratioOk = second == int.MaxValue || best < LoweRatio * second;
            result[q] = (bestIdx, ratioOk);
        }

        return result;
    }

    private static double[] UnscaleToFullResolution(double[] hDown, double scaleA, double scaleB)
    {
        // H_full = diag(scaleA) * H_down * diag(1/scaleB)
        var sa = new[] { scaleA, 0, 0, 0, scaleA, 0, 0, 0, 1.0 };
        var sbInv = new[] { 1.0 / scaleB, 0, 0, 0, 1.0 / scaleB, 0, 0, 0, 1.0 };
        var full = LinearAlgebra.Mat3Mul(LinearAlgebra.Mat3Mul(sa, hDown), sbInv);
        if (Math.Abs(full[8]) > 1e-12)
        {
            for (var i = 0; i < 9; i++)
            {
                full[i] /= full[8];
            }
        }

        return full;
    }
}
