using Blocwerk.Core.Abstractions;
using OpenCvSharp;

namespace Blocwerk.HoldDetection.Tests.Outlines;

/// <summary>Outline behaviour on crops of the owner's real wall photos (see <see cref="HoldFixtures"/>).</summary>
public class HoldOutlineRealPhotoTests
{
    [Theory]
    [InlineData("green")]
    [InlineData("white")]
    [InlineData("shadowGreen")]
    [InlineData("shadowBlue")]
    [InlineData("donut2783")]
    [InlineData("crescent2783")]
    public void DistinctHold_GetsTightPolygonAroundSeed(string key)
    {
        HoldFixture f = Pick(key);
        var (result, _, w, h) = HoldFixtures.Outline(f);

        Assert.Equal(HoldOutlineMethod.Contour, result.Method);
        Assert.True(result.Confidence >= 0.6, $"confidence {result.Confidence}");
        Assert.InRange(result.Polygon.Count, 3, 24);
        Assert.NotNull(result.ShapePoints);
        Assert.Equal(result.Polygon.Count, result.ShapePoints!.Count);

        Point[] poly = ToPixels(result.Polygon, w, h);
        Assert.True(Cv2.PointPolygonTest(poly, new Point2f((float)f.Centre.X, (float)f.Centre.Y), false) >= 0, "polygon must contain the seed centre");

        // Not leaking: the outline fills a plausible share of the (tight) box ellipse and stays near the box.
        double ellipse = Math.PI * f.Width * f.Height / 4.0;
        double ratio = Cv2.ContourArea(poly) / ellipse;
        Assert.InRange(ratio, 0.45, 1.35);
        Rect bb = Cv2.BoundingRect(poly);
        Assert.True(bb.Left >= f.Left - (0.2 * f.Width) && bb.Right <= f.Left + (1.2 * f.Width), $"x-extent {bb}");
        Assert.True(bb.Top >= f.Top - (0.2 * f.Height) && bb.Bottom <= f.Top + (1.2 * f.Height), $"y-extent {bb}");
    }

    [Fact]
    public void WhiteChalkyHold_IsOutlinedNotFallback()
    {
        var (result, _, _, _) = HoldFixtures.Outline(HoldFixtures.White);

        Assert.Equal(HoldOutlineMethod.Contour, result.Method);
        Assert.True(result.Fingerprint.Histogram[HoldFingerprint.HueBins] > 0.5, "a white hold is mostly neutral");
    }

    [Theory]
    [InlineData("bigVolume")]
    [InlineData("smallVolume")]
    public void WoodenVolume_FallsBackToCircle_WithLowConfidence(string key)
    {
        var (result, _, _, _) = HoldFixtures.Outline(Pick(key));

        Assert.Equal(HoldOutlineMethod.CircleFallback, result.Method);
        Assert.True(result.Confidence <= 0.2, $"confidence {result.Confidence}");
        Assert.Null(result.ShapePoints);
    }

    [Fact]
    public void ShadowBelowHoldOnOverhang_IsNotPartOfTheOutline()
    {
        // The green hold's box ends at y=240; its cast shadow runs ~20 px further down the plywood.
        HoldFixture f = HoldFixtures.ShadowGreen;
        var (result, _, w, h) = HoldFixtures.Outline(f);

        Rect bb = Cv2.BoundingRect(ToPixels(result.Polygon, w, h));
        Assert.True(bb.Bottom <= f.Top + f.Height + 6, $"outline bottom {bb.Bottom} runs into the shadow (box bottom {f.Top + f.Height})");
    }

    [Fact]
    public void HoldTouchingAMarker_DoesNotSwallowIt()
    {
        // The yellow donut in IMG_2770 touches a black-and-white ArUco marker to its right.
        var (result, _, w, h) = HoldFixtures.Outline(HoldFixtures.Donut2770);
        Point[] poly = ToPixels(result.Polygon, w, h);

        Assert.NotEqual(HoldOutlineMethod.CircleFallback, result.Method);
        Assert.True(Cv2.PointPolygonTest(poly, new Point2f(470, 330), false) < 0, "outline covers the marker's black border");
        Assert.True(Cv2.PointPolygonTest(poly, new Point2f(560, 300), false) < 0, "outline covers the marker's centre");
    }

    internal static Point[] ToPixels(IReadOnlyList<NormalizedPoint> polygon, int w, int h) =>
        polygon.Select(p => new Point((int)Math.Round(p.X * w), (int)Math.Round(p.Y * h))).ToArray();

    private static HoldFixture Pick(string key) => key switch
    {
        "green" => HoldFixtures.Green,
        "white" => HoldFixtures.White,
        "shadowGreen" => HoldFixtures.ShadowGreen,
        "shadowBlue" => HoldFixtures.ShadowBlue,
        "donut2783" => HoldFixtures.Donut2783,
        "crescent2783" => HoldFixtures.Crescent2783,
        "bigVolume" => HoldFixtures.BigVolume,
        "smallVolume" => HoldFixtures.SmallVolume,
        _ => throw new ArgumentOutOfRangeException(nameof(key)),
    };
}
