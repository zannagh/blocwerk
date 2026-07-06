using Blocwerk.HoldDetection.Alignment;
using SkiaSharp;

namespace Blocwerk.HoldDetection.Tests;

public class ImageAlignmentTests
{
    private static readonly string WallsDir = Path.Combine(AppContext.BaseDirectory, "walls");

    [SkippableFact]
    public void Estimate_RecoversKnownTransform()
    {
        var path = Path.Combine(WallsDir, "Test-Wall.jpeg");
        Skip.IfNot(File.Exists(path), $"Missing test image: {path}");

        var originalBytes = File.ReadAllBytes(path);
        using var orig = SKBitmap.Decode(originalBytes);
        Assert.NotNull(orig);

        // Known affine: 4 degree rotation, 0.97 scale, (35, 20) translation.
        const double angle = 4.0 * Math.PI / 180.0;
        const double s = 0.97;
        var a = s * Math.Cos(angle);
        var b = -s * Math.Sin(angle);
        var c = 35.0;
        var d = s * Math.Sin(angle);
        var e = s * Math.Cos(angle);
        var f = 20.0;

        var warpedBytes = RenderWarped(orig, a, b, c, d, e, f);

        // H maps warped pixels -> original pixels, i.e. the inverse of the applied transform.
        var h = ImageAlignment.Estimate(originalBytes, warpedBytes);
        Assert.NotNull(h);
        Assert.True(h!.Inliers >= 15, $"too few inliers: {h.Inliers}");

        foreach (var (px, py) in SamplePoints(orig.Width, orig.Height))
        {
            // Forward transform of an original point into the warped frame.
            var qx = (a * px) + (b * py) + c;
            var qy = (d * px) + (e * py) + f;
            var (rx, ry) = h.Project(qx, qy);
            var err = Math.Sqrt(((rx - px) * (rx - px)) + ((ry - py) * (ry - py)));
            Assert.True(err < 20, $"point ({px},{py}) mapped back with error {err:F1}px");
        }

        // The normalized variant (used by wall auto-align) maps [0,1] -> [0,1].
        var hn = ImageAlignment.EstimateNormalized(originalBytes, warpedBytes);
        Assert.NotNull(hn);
        foreach (var (px, py) in SamplePoints(orig.Width, orig.Height))
        {
            var qx = ((a * px) + (b * py) + c) / orig.Width;
            var qy = ((d * px) + (e * py) + f) / orig.Height;
            var (rx, ry) = hn!.Project(qx, qy);
            var errX = Math.Abs(rx - (px / orig.Width));
            var errY = Math.Abs(ry - (py / orig.Height));
            Assert.True(errX < 0.02 && errY < 0.02, $"normalized error ({errX:F3},{errY:F3})");
        }
    }

    private static byte[] RenderWarped(SKBitmap orig, double a, double b, double c, double d, double e, double f)
    {
        var info = new SKImageInfo(orig.Width, orig.Height);
        using var surface = SKSurface.Create(info);
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Black);
        var m = SKMatrix.CreateIdentity();
        m.ScaleX = (float)a;
        m.SkewX = (float)b;
        m.TransX = (float)c;
        m.SkewY = (float)d;
        m.ScaleY = (float)e;
        m.TransY = (float)f;
        canvas.SetMatrix(m);
        canvas.DrawBitmap(orig, 0, 0);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        return data.ToArray();
    }

    private static IEnumerable<(double X, double Y)> SamplePoints(int w, int h)
    {
        yield return (w * 0.35, h * 0.35);
        yield return (w * 0.6, h * 0.45);
        yield return (w * 0.5, h * 0.6);
    }
}
