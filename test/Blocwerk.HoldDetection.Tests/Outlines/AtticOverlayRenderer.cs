using Blocwerk.Core.Detection.Outlines;
using OpenCvSharp;

namespace Blocwerk.HoldDetection.Tests.Outlines;

/// <summary>
/// Draws a "circle today vs. outline after the upgrade" overlay of the densest part of a wall photo. The
/// JPEG is re-encoded from pixels by OpenCV, so none of the source's EXIF (GPS) survives, and the quality is
/// lowered until the file fits <see cref="MaxBytes"/>.
/// </summary>
internal static class AtticOverlayRenderer
{
    public const int MaxBytes = 290_000;

    private const int CropWidth = 1100;
    private const int CropHeight = 800;

    public static void Write(string photoPath, IReadOnlyList<HoldOutlineUpgradeProposal> proposals, string outPath)
    {
        using Mat img = Cv2.ImRead(photoPath, ImreadModes.Color);
        int w = img.Width, h = img.Height;
        var outlined = proposals.Where(p => p.Outcome == HoldOutlineUpgradeOutcome.Outline).ToList();
        var window = DensestWindow(outlined, w, h);
        foreach (var p in proposals)
        {
            // The circle exactly as HoldShape.razor renders it: an ellipse rx = R·W, ry = R·H.
            var centre = new Point((int)(p.Hold.X * w), (int)(p.Hold.Y * h));
            var axes = new Size((int)(p.Hold.Radius * w), (int)(p.Hold.Radius * h));
            Cv2.Ellipse(img, centre, axes, 0, 0, 360, new Scalar(255, 0, 255), 2);
            if (p.Outcome != HoldOutlineUpgradeOutcome.Outline)
            {
                continue;
            }

            Cv2.Polylines(img, [Ring(p.Result.Polygon.Select(q => (q.X, q.Y)), w, h)], true, new Scalar(0, 255, 0), 3);
            foreach (var hole in p.Result.ShapeHoles ?? [])
            {
                var ring = hole.Select(sp => (p.Result.AnchorX + sp.Dx, p.Result.AnchorY + sp.Dy));
                Cv2.Polylines(img, [Ring(ring, w, h)], true, new Scalar(255, 160, 0), 2);
            }
        }

        using var crop = new Mat(img, window);
        Cv2.PutText(crop, "magenta = circle today, green = new outline, blue = pocket", new Point(10, 30),
            HersheyFonts.HersheySimplex, 0.8, new Scalar(255, 255, 255), 2);
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        for (int quality = 85; quality >= 40; quality -= 5)
        {
            Cv2.ImEncode(".jpg", crop, out byte[] bytes, new ImageEncodingParam(ImwriteFlags.JpegQuality, quality));
            if (bytes.Length <= MaxBytes || quality == 40)
            {
                File.WriteAllBytes(outPath, bytes);
                return;
            }
        }
    }

    private static Point[] Ring(IEnumerable<(double X, double Y)> points, int w, int h) =>
        points.Select(q => new Point((int)(q.X * w), (int)(q.Y * h))).ToArray();

    /// <summary>The crop window (on a 100 px grid) containing the most outlined holds.</summary>
    private static Rect DensestWindow(List<HoldOutlineUpgradeProposal> outlined, int w, int h)
    {
        int cw = Math.Min(CropWidth, w), ch = Math.Min(CropHeight, h);
        var best = new Rect(0, 0, cw, ch);
        int bestCount = -1;
        for (int y = 0; y + ch <= h; y += 100)
        {
            for (int x = 0; x + cw <= w; x += 100)
            {
                int count = outlined.Count(p => p.Hold.X * w >= x && p.Hold.X * w < x + cw && p.Hold.Y * h >= y && p.Hold.Y * h < y + ch);
                if (count > bestCount)
                {
                    (best, bestCount) = (new Rect(x, y, cw, ch), count);
                }
            }
        }

        return best;
    }
}
