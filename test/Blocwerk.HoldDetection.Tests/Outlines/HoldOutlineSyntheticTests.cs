using System.Diagnostics;
using System.Globalization;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Detection.Outlines;
using Blocwerk.HoldDetection.Outlines;
using OpenCvSharp;
using Xunit.Abstractions;

namespace Blocwerk.HoldDetection.Tests.Outlines;

/// <summary>Outlines against synthetic holds with known masks, the ShapePoint convention, and timing.</summary>
public class HoldOutlineSyntheticTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(40, 40, 200, 0)] // red hold
    [InlineData(200, 90, 30, 0)] // blue hold
    [InlineData(40, 40, 200, 22)] // red hold with a cast shadow below it
    [InlineData(235, 238, 240, 16)] // white hold with a shadow
    public void SyntheticBlob_OnWood_MatchesKnownMask(int b, int g, int r, int shadowDrop)
    {
        using Mat img = SyntheticWall.Wood(600, 500);
        Point[] truth = SyntheticWall.Blob(new Point(300, 240), 90, 60, seed: b + g + r);
        SyntheticWall.PaintHold(img, truth, new Scalar(b, g, r), shadowDrop);
        Rect box = Cv2.BoundingRect(truth);
        HoldSeed seed = HoldSeed.FromPixelBox(box.X, box.Y, box.Width, box.Height, img.Width, img.Height);

        using var session = new OpenCvHoldOutlineService().OpenSession(img);
        HoldOutlineResult result = session.Outline(seed);

        Point[] poly = HoldOutlineRealPhotoTests.ToPixels(result.Polygon, img.Width, img.Height);
        double iou = SyntheticWall.IoU(truth, poly, img.Size());
        Assert.True(iou >= 0.85, $"IoU {iou:F3} ({result.Method}, conf {result.Confidence})");
        Assert.Equal(HoldOutlineMethod.Contour, result.Method);
        Assert.InRange(result.AreaPx / Cv2.ContourArea(truth), 0.85, 1.15);
    }

    [Fact]
    public void ShapePoints_FollowTheRendererConvention_OnANonSquareImage()
    {
        // 4:3 like the phone photos, so a wrong axis normalization would show up.
        using Mat img = SyntheticWall.Wood(800, 600);
        Point[] truth = SyntheticWall.Blob(new Point(520, 210), 70, 45, seed: 3);
        SyntheticWall.PaintHold(img, truth, new Scalar(30, 160, 60), 0);
        Rect box = Cv2.BoundingRect(truth);
        HoldSeed seed = HoldSeed.FromPixelBox(box.X, box.Y, box.Width, box.Height, img.Width, img.Height);
        using var session = new OpenCvHoldOutlineService().OpenSession(img);
        HoldOutlineResult result = session.Outline(seed);

        Assert.NotNull(result.ShapePoints);
        Assert.Equal(seed.X, result.AnchorX);
        Assert.Equal(seed.Y, result.AnchorY);

        // Exactly what HoldShape.razor emits: "((X + Dx)·100, (Y + Dy)·100)" in a 0-100 viewBox stretched
        // over the image (preserveAspectRatio="none"), i.e. x·W/100 and y·H/100 pixels.
        for (int i = 0; i < result.ShapePoints!.Count; i++)
        {
            var sp = result.ShapePoints[i];
            double vx = double.Parse(((seed.X + sp.Dx) * 100).ToString("F2", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
            double vy = double.Parse(((seed.Y + sp.Dy) * 100).ToString("F2", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
            Assert.InRange((vx * img.Width / 100) - (result.Polygon[i].X * img.Width), -0.5, 0.5);
            Assert.InRange((vy * img.Height / 100) - (result.Polygon[i].Y * img.Height), -0.5, 0.5);
        }

        // Round trip through the geometry helper.
        var back = HoldOutlineGeometry.ToPolygon(result.ShapePoints, seed.X, seed.Y);
        for (int i = 0; i < back.Count; i++)
        {
            Assert.Equal(result.Polygon[i].X, back[i].X, 4);
            Assert.Equal(result.Polygon[i].Y, back[i].Y, 4);
        }

        // And the rendered polygon really lands on the painted hold.
        Point[] rendered = back.Select(p => new Point((int)(p.X * img.Width), (int)(p.Y * img.Height))).ToArray();
        Assert.True(SyntheticWall.IoU(truth, rendered, img.Size()) >= 0.85);
    }

    [Fact]
    public void SeedOffTheImage_ReturnsZeroConfidenceFallback()
    {
        using Mat img = SyntheticWall.Wood(200, 200);
        using var session = new OpenCvHoldOutlineService().OpenSession(img);

        HoldOutlineResult result = session.Outline(new HoldSeed(1.4, 0.5, 0.05));

        Assert.Equal(HoldOutlineMethod.CircleFallback, result.Method);
        Assert.Equal(0, result.Confidence);
        Assert.Null(result.ShapePoints);
    }

    [Fact]
    public void FullResolutionPhoto_OutlinesEachHoldWithinBudget()
    {
        using Mat img = SyntheticWall.Wood(4032, 3024);
        var rng = new Random(11);
        var seeds = new List<HoldSeed>();
        Scalar[] palette = [new(40, 40, 200), new(200, 90, 30), new(40, 190, 230), new(60, 170, 60), new(235, 238, 240), new(40, 40, 40)];
        for (int gy = 0; gy < 8; gy++)
        {
            for (int gx = 0; gx < 10; gx++)
            {
                int rx = rng.Next(20, 130), ry = rng.Next(20, 130);
                var c = new Point(200 + (gx * 400), 200 + (gy * 360));
                Point[] blob = SyntheticWall.Blob(c, rx, ry, rng.Next());
                SyntheticWall.PaintHold(img, blob, palette[rng.Next(palette.Length)], rng.Next(0, 20));
                Rect b = Cv2.BoundingRect(blob);
                seeds.Add(HoldSeed.FromPixelBox(b.X, b.Y, b.Width, b.Height, img.Width, img.Height));
            }
        }

        using var session = new OpenCvHoldOutlineService().OpenSession(img);
        session.Outline(seeds[0]); // warm-up (JIT, native load)
        var sw = Stopwatch.StartNew();
        var methods = seeds.Select(s => session.Outline(s).Method).ToList();
        sw.Stop();

        double perHold = sw.Elapsed.TotalMilliseconds / seeds.Count;
        output.WriteLine($"PERF {seeds.Count} holds on 4032x3024: {perHold:F2} ms/hold; contour={methods.Count(m => m == HoldOutlineMethod.Contour)}");
        Assert.True(methods.Count(m => m == HoldOutlineMethod.Contour) >= seeds.Count * 0.9);

        // Wall-clock budgets are only meaningful on an idle machine: inside a full parallel test run
        // this measured ~138 ms/hold against ~7 ms alone. Assert the budget only when asked to.
        if (Environment.GetEnvironmentVariable("BLOCWERK_PERF_ASSERT") == "1")
        {
            Assert.True(perHold < 25, $"{perHold:F1} ms per hold");
        }
    }
}
