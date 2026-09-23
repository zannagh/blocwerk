using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Detection.Outlines;
using Blocwerk.Core.Entities;
using Blocwerk.HoldDetection.Outlines;
using OpenCvSharp;
using Xunit.Abstractions;

namespace Blocwerk.HoldDetection.Tests.Outlines;

/// <summary>Pocket / donut holds: interior holes are kept (conservatively) instead of filled.</summary>
public class HoldOutlinePocketTests(ITestOutputHelper output)
{
    private const int HoleRadius = 26;

    private static readonly Point BlobCentre = new(300, 240);
    private static readonly Point HoleCentre = new(312, 232);

    // Off the seed core: a dark pocket AT the core would be sampled as the hold colour itself.
    private static readonly Point PocketCentre = new(350, 250);

    [Fact]
    public void SyntheticDonut_ThroughHole_IsKeptAsOneRing()
    {
        using Mat img = PaintDonut(HoleFill.Wall, out Point[] outer, out Point[] hole);
        HoldOutlineResult result = Outline(img, outer);

        Assert.Equal(HoldOutlineMethod.Contour, result.Method);
        Assert.NotNull(result.ShapeHoles);
        Assert.Single(result.ShapeHoles!);
        Assert.InRange(result.ShapeHoles![0].Count, 3, HoleFinder.MaxHoleVertices);

        Point[] ring = ToPixels(result.ShapeHoles[0], result, img);
        double iou = SyntheticWall.IoU(hole, ring, img.Size());
        output.WriteLine($"hole IoU {iou:F3}");
        Assert.True(iou >= 0.6, $"hole IoU {iou:F3}");

        // The reported area is the RING (outer minus hole); the fingerprint keeps the filled area.
        double ringArea = Cv2.ContourArea(outer) - Cv2.ContourArea(hole);
        Assert.InRange(result.AreaPx / ringArea, 0.85, 1.15);
        Assert.True(result.Fingerprint.AreaPx > result.AreaPx, "fingerprint must be measured on the filled outline");
    }

    [Fact]
    public void SyntheticDeepDarkPocket_IsKeptAsAHole()
    {
        using Mat img = PaintDonut(HoleFill.DarkPocket, out Point[] outer, out Point[] hole, PocketCentre);
        HoldOutlineResult result = Outline(img, outer);

        Assert.Equal(HoldOutlineMethod.Contour, result.Method);
        Assert.Single(result.ShapeHoles ?? []);
        Assert.True(SyntheticWall.IoU(hole, ToPixels(result.ShapeHoles![0], result, img), img.Size()) >= 0.6);
    }

    [Theory]
    [InlineData(40, 40, 200)]
    [InlineData(200, 90, 30)]
    [InlineData(235, 238, 240)]
    [InlineData(40, 40, 40)]
    public void SolidSyntheticHold_HasNoHole(int b, int g, int r)
    {
        using Mat img = SyntheticWall.Wood(600, 500);
        Point[] outer = SyntheticWall.Blob(BlobCentre, 90, 60, seed: b + g + r);
        SyntheticWall.PaintHold(img, outer, new Scalar(b, g, r), 14);

        Assert.Null(Outline(img, outer).ShapeHoles);
    }

    [Theory]
    [InlineData("green")]
    [InlineData("white")]
    [InlineData("shadowGreen")]
    [InlineData("shadowBlue")]
    [InlineData("yellowBall")]
    [InlineData("crescent2770")]
    [InlineData("crescent2783")]
    public void RealSolidHolds_GetNoHole(string key)
    {
        var (result, _, _, _) = HoldFixtures.Outline(Pick(key));

        Assert.Null(result.ShapeHoles);
    }

    [Fact]
    public void RealDonutNextToMarker_GetsItsHoleInTheRightPlace()
    {
        var (result, _, w, h) = HoldFixtures.Outline(HoldFixtures.Donut2770);

        Assert.Equal(HoldOutlineMethod.Contour, result.Method);
        Assert.Single(result.ShapeHoles ?? []);
        Point[] ring = HoldOutlineGeometry.ToPolygon(result.ShapeHoles![0], result.AnchorX, result.AnchorY)
            .Select(p => new Point((int)Math.Round(p.X * w), (int)Math.Round(p.Y * h)))
            .ToArray();
        output.WriteLine($"donut hole bbox {Cv2.BoundingRect(ring)}");

        // The through-hole in donut_2770.jpg sits around (323, 327), ~30 px across; the yellow body is left of it.
        Assert.True(Cv2.PointPolygonTest(ring, new Point2f(323, 327), false) >= 0, "hole ring must contain the hole centre");
        Assert.True(Cv2.PointPolygonTest(ring, new Point2f(250, 330), false) < 0, "hole ring must not cover the yellow body");
        Assert.InRange(Cv2.ContourArea(ring), 800, 6000);
    }

    [Fact]
    public void Fingerprint_IsNearIdentical_WithAndWithoutTheHoleDetected()
    {
        // Same hold twice: once the hole shows the wall (detected), once its floor reads as hold (not detected).
        using Mat withHole = PaintDonut(HoleFill.Wall, out Point[] outer, out _);
        using Mat noHole = PaintDonut(HoleFill.HoldShade, out _, out _);
        HoldOutlineResult a = Outline(withHole, outer);
        HoldOutlineResult b = Outline(noHole, outer);

        Assert.Single(a.ShapeHoles ?? []);
        Assert.Null(b.ShapeHoles);
        double similarity = HoldFingerprint.Similarity(a.Fingerprint, b.Fingerprint);
        output.WriteLine($"similarity {similarity:F3}; areas {a.Fingerprint.AreaPx} vs {b.Fingerprint.AreaPx}");
        Assert.True(similarity >= 0.9, $"similarity {similarity:F3}");
        Assert.InRange(a.Fingerprint.AreaPx / b.Fingerprint.AreaPx, 0.95, 1.05);
    }

    private enum HoleFill
    {
        Wall,
        DarkPocket,
        HoldShade,
    }

    private static Mat PaintDonut(HoleFill fill, out Point[] outer, out Point[] hole, Point? holeCentre = null)
    {
        Point c = holeCentre ?? HoleCentre;
        Mat img = SyntheticWall.Wood(600, 500);
        using Mat wall = img.Clone();
        outer = SyntheticWall.Blob(BlobCentre, 90, 60, seed: 5);
        SyntheticWall.PaintHold(img, outer, new Scalar(40, 40, 200), 0);
        hole = Enumerable.Range(0, 32)
            .Select(k => k * 2 * Math.PI / 32)
            .Select(a => new Point(c.X + (int)Math.Round(HoleRadius * Math.Cos(a)), c.Y + (int)Math.Round(HoleRadius * 0.85 * Math.Sin(a))))
            .ToArray();
        using var holeMask = new Mat(img.Size(), MatType.CV_8UC1, Scalar.All(0));
        Cv2.FillPoly(holeMask, new[] { hole }, Scalar.All(255));
        switch (fill)
        {
            case HoleFill.Wall:
                wall.CopyTo(img, holeMask);
                break;
            case HoleFill.DarkPocket:
                img.SetTo(new Scalar(22, 20, 30), holeMask);
                break;
            default:
                img.SetTo(new Scalar(34, 34, 175), holeMask);
                break;
        }

        return img;
    }

    private static HoldOutlineResult Outline(Mat img, Point[] outer)
    {
        Rect box = Cv2.BoundingRect(outer);
        HoldSeed seed = HoldSeed.FromPixelBox(box.X, box.Y, box.Width, box.Height, img.Width, img.Height);
        using var session = new OpenCvHoldOutlineService().OpenSession(img);
        return session.Outline(seed);
    }

    private static Point[] ToPixels(List<ShapePoint> ring, HoldOutlineResult result, Mat img) =>
        HoldOutlineGeometry.ToPolygon(ring, result.AnchorX, result.AnchorY)
            .Select(p => new Point((int)Math.Round(p.X * img.Width), (int)Math.Round(p.Y * img.Height)))
            .ToArray();

    private static HoldFixture Pick(string key) => key switch
    {
        "green" => HoldFixtures.Green,
        "white" => HoldFixtures.White,
        "shadowGreen" => HoldFixtures.ShadowGreen,
        "shadowBlue" => HoldFixtures.ShadowBlue,
        "yellowBall" => HoldFixtures.YellowBall,
        "crescent2770" => HoldFixtures.Crescent2770,
        "crescent2783" => HoldFixtures.Crescent2783,
        _ => throw new ArgumentOutOfRangeException(nameof(key)),
    };
}
