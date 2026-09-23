using Blocwerk.Core.Abstractions;
using OpenCvSharp;

namespace Blocwerk.HoldDetection.Tests.Outlines;

/// <summary>Draws the seed circle (as the app renders it today) next to the detected outline.</summary>
internal static class OutlineDemoRenderer
{
    /// <summary>Writes a side-by-side "before (circle) / after (outline)" image.</summary>
    public static void Write(Mat img, HoldSeed seed, HoldOutlineResult result, string path)
    {
        using var before = img.Clone();
        using var after = img.Clone();
        int w = img.Width, h = img.Height;
        var centre = new Point((int)(seed.X * w), (int)(seed.Y * h));
        int r = (int)(seed.Radius * Math.Max(w, h));
        Cv2.Circle(before, centre, r, new Scalar(255, 0, 255), 3);
        Cv2.PutText(before, "circle (today)", new Point(8, 22), HersheyFonts.HersheySimplex, 0.6, new Scalar(255, 0, 255), 2);
        var poly = result.Polygon.Select(p => new Point((int)(p.X * w), (int)(p.Y * h))).ToArray();
        Cv2.Polylines(after, new[] { poly }, true, new Scalar(0, 255, 0), 3);
        foreach (var p in poly)
        {
            Cv2.Circle(after, p, 4, new Scalar(0, 0, 255), -1);
        }

        string label = $"{result.Method} conf {result.Confidence:F2}";
        Cv2.PutText(after, label, new Point(8, 22), HersheyFonts.HersheySimplex, 0.6, new Scalar(0, 160, 0), 2);
        using var both = new Mat();
        Cv2.HConcat(new[] { before, after }, both);
        Cv2.ImWrite(path, both);
    }
}
