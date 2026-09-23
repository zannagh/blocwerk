using System.Runtime.InteropServices;
using Blocwerk.Core.Abstractions;
using Blocwerk.HoldDetection.Matching;
using Blocwerk.HoldDetection.Tests.Markers;
using OpenCvSharp;

namespace Blocwerk.HoldDetection.Tests.Matching;

/// <summary>
/// The matcher with a marker seed: the seed replaces a failed texture homography, and a seeded run reads
/// the images in the RAW pixel frame — the frame the seed, the hold X/Y and the marker corners share.
/// </summary>
public class SeededMatcherTests
{
    // Right = left under a mild projective map (shift + scale + perspective), in raw pixels.
    private static readonly double[,] LeftToRight =
    {
        { 0.92, 0.03, -140 },
        { -0.02, 0.95, 40 },
        { 0.00002, 0.00001, 1 },
    };

    [Fact]
    public void NoSharedTexture_TextureHomographyIsLost_ButTheSeedMatchesEveryHold()
    {
        // Identical holds, and a background that shares NO texture between the photos (the wide-baseline
        // case: the wood grain seen from 2.6 m apart does not match) — only the holds are common.
        var scene = Scene(sharedTexture: false);

        // Guard that the scene exercises the failure: without a seed the run throws or mostly misses.
        int unseeded;
        try
        {
            var plain = new OpenCvHoldOverlapMatcher().Match(
                scene.Left, scene.LeftHolds, scene.Right, scene.RightHolds, HoldOverlapDirection.Right);
            unseeded = plain.Proposals.Count(p => p.LeftHoldId == p.RightHoldId);
        }
        catch (InvalidOperationException)
        {
            unseeded = 0;
        }

        Assert.True(unseeded < scene.LeftHolds.Count / 2, $"unseeded run matched {unseeded}: scene too easy");

        var result = new OpenCvHoldOverlapMatcher().Match(
            scene.Left, scene.LeftHolds, scene.Right, scene.RightHolds, HoldOverlapDirection.Right, null, Seed(scene));

        AssertAllMatched(scene, result);
    }

    [Fact]
    public void Orientation6RightPhoto_SeededRunWorksInTheRawFrame()
    {
        var scene = Scene(sharedTexture: true);
        var rotated = ExifOrientationFrameTests.WithExifOrientation(scene.Right, 6);

        // The trap: a plain decode rotates the photo, so its frame no longer matches hold X/Y or the seed.
        using (var plain = SeededCoarse.Decode(rotated, seeded: false))
        using (var raw = SeededCoarse.Decode(rotated, seeded: true))
        {
            Assert.Equal((scene.RightWidth, scene.RightHeight), (raw.Width, raw.Height));
            Assert.Equal((scene.RightHeight, scene.RightWidth), (plain.Width, plain.Height));
        }

        var result = new OpenCvHoldOverlapMatcher().Match(
            scene.Left, scene.LeftHolds, rotated, scene.RightHolds, HoldOverlapDirection.Right, null, Seed(scene));

        AssertAllMatched(scene, result);
    }

    [Fact]
    public void SeedToPixels_IsTheNormalizedHomographyRescaled()
    {
        var hn = Normalized(LeftToRight, 1000, 800, 900, 700);
        var back = SeededCoarse.ToPixels(hn, 1000, 800, 900, 700);
        var p = HomographyHelper.Warp(back, new Pt(300, 200));
        var q = HomographyHelper.Warp(LeftToRight, new Pt(300, 200));
        Assert.Equal(q.X, p.X, 6);
        Assert.Equal(q.Y, p.Y, 6);
    }

    private static void AssertAllMatched(SyntheticPair scene, HoldOverlapResult result)
    {
        int correct = result.Proposals.Count(p => p.LeftHoldId == p.RightHoldId);
        Assert.Equal(result.Proposals.Count, correct);
        Assert.True(correct >= scene.LeftHolds.Count - 2, $"matched {correct}/{scene.LeftHolds.Count}");
    }

    private static HoldOverlapSeed Seed(SyntheticPair s) => new()
    {
        Homography = Normalized(LeftToRight, s.LeftWidth, s.LeftHeight, s.RightWidth, s.RightHeight),
        Source = HoldOverlapSeedSource.SharedMarkers,
        MarkerCount = 3,
    };

    /// <summary>Hn = S_R⁻¹ · H · S_L with S = diag(w, h, 1).</summary>
    private static double[] Normalized(double[,] h, int wl, int hl, int wr, int hr)
    {
        double[] rowScale = [1.0 / wr, 1.0 / hr, 1];
        double[] colScale = [wl, hl, 1];
        var hn = new double[9];
        for (int r = 0; r < 3; r++)
        {
            for (int c = 0; c < 3; c++)
            {
                hn[(r * 3) + c] = h[r, c] * rowScale[r] * colScale[c];
            }
        }

        return hn;
    }

    /// <summary>
    /// A grid of identical red "holds" on a flat (or noisy) wall; the right photo is the left one warped by
    /// <see cref="LeftToRight"/>. Hold ids are equal for the same physical hold on both sides.
    /// </summary>
    private static SyntheticPair Scene(bool sharedTexture)
    {
        const int w = 1200, h = 900;
        using var left = new Mat(h, w, MatType.CV_8UC3, new Scalar(170, 190, 205));
        if (sharedTexture)
        {
            AddNoise(left, 1);
        }

        var centres = new List<Point2d>();
        for (int gx = 0; gx < 7; gx++)
        {
            for (int gy = 0; gy < 5; gy++)
            {
                var c = new Point2d(250 + (gx * 120) + (gy % 2 * 30), 150 + (gy * 140));
                centres.Add(c);
                Cv2.Circle(left, (int)c.X, (int)c.Y, 22, new Scalar(40, 40, 200), -1, LineTypes.AntiAlias);
            }
        }

        using var hm = new Mat(3, 3, MatType.CV_64FC1);
        for (int r = 0; r < 3; r++)
        {
            for (int c = 0; c < 3; c++)
            {
                hm.Set(r, c, LeftToRight[r, c]);
            }
        }

        using var right = new Mat();
        Cv2.WarpPerspective(left, right, hm, new Size(w, h), InterpolationFlags.Linear, BorderTypes.Replicate);
        if (!sharedTexture)
        {
            AddNoise(left, 2);
            AddNoise(right, 3);
        }

        var leftHolds = centres.Select((c, i) => new MatcherHold(i, c.X / w, c.Y / h, 22.0 / w)).ToList();
        var rightHolds = centres.Select((c, i) =>
        {
            var p = HomographyHelper.Warp(LeftToRight, new Pt(c.X, c.Y));
            return new MatcherHold(i, p.X / w, p.Y / h, 20.0 / w);
        }).Where(m => m.X is > 0.02 and < 0.98 && m.Y is > 0.02 and < 0.98).ToList();

        return new SyntheticPair(left.ImEncode(".jpg"), right.ImEncode(".jpg"), leftHolds, rightHolds, w, h, w, h);
    }

    /// <summary>Deterministic blurred noise texture added to the whole image.</summary>
    private static void AddNoise(Mat img, int seed)
    {
        var rng = new Random(seed);
        var bytes = new byte[img.Rows * img.Cols * 3];
        rng.NextBytes(bytes);
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)(bytes[i] % 60);
        }

        using var noise = new Mat(img.Rows, img.Cols, MatType.CV_8UC3);
        Marshal.Copy(bytes, 0, noise.Data, bytes.Length);
        Cv2.GaussianBlur(noise, noise, new Size(3, 3), 0);
        Cv2.Add(img, noise, img);
    }

    private sealed record SyntheticPair(
        byte[] Left,
        byte[] Right,
        List<MatcherHold> LeftHolds,
        List<MatcherHold> RightHolds,
        int LeftWidth,
        int LeftHeight,
        int RightWidth,
        int RightHeight);
}
