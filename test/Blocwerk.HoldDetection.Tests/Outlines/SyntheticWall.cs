using OpenCvSharp;

namespace Blocwerk.HoldDetection.Tests.Outlines;

/// <summary>Draws plywood-like backgrounds with holds (and their shadows) whose true masks are known.</summary>
internal static class SyntheticWall
{
    /// <summary>A light spruce-like background with horizontal grain and sensor noise.</summary>
    public static Mat Wood(int width, int height, int seed = 7)
    {
        var rng = new Random(seed);
        using var f = new Mat(height, width, MatType.CV_32FC3, new Scalar(168, 192, 214));
        using var noise = new Mat(height, width, MatType.CV_32FC3);
        Cv2.Randn(noise, new Scalar(0, 0, 0), new Scalar(4, 4, 4));
        Cv2.Add(f, noise, f);
        for (int y = 0; y < height; y++)
        {
            double g = (6 * Math.Sin(y / 7.0)) + (4 * Math.Sin((y / 23.0) + 1.3)) + (rng.NextDouble() * 3);
            using var row = f.Row(y);
            Cv2.Add(row, new Scalar(g, g, g), row);
        }

        var img = new Mat();
        f.ConvertTo(img, MatType.CV_8UC3);
        return img;
    }

    /// <summary>An irregular hold outline (a lumpy ellipse) around a centre.</summary>
    public static Point[] Blob(Point centre, int rx, int ry, int seed)
    {
        var rng = new Random(seed);
        double phase = rng.NextDouble() * Math.PI;
        var pts = new Point[36];
        for (int k = 0; k < pts.Length; k++)
        {
            double a = k * 2 * Math.PI / pts.Length;
            double lump = 1 + (0.12 * Math.Sin((3 * a) + phase)) + (0.06 * Math.Cos((5 * a) - phase));
            pts[k] = new Point(centre.X + (int)(rx * lump * Math.Cos(a)), centre.Y + (int)(ry * lump * Math.Sin(a)));
        }

        return pts;
    }

    /// <summary>
    /// Paints a hold: first its shadow (the outline shifted down by <paramref name="shadowDrop"/>, the wood
    /// darkened ×0.65), then the hold itself in <paramref name="colour"/> with a little texture.
    /// </summary>
    public static void PaintHold(Mat img, Point[] outline, Scalar colour, int shadowDrop)
    {
        // Paint inside the hold's (and shadow's) bounding box only, so a full-res canvas stays cheap.
        Rect box = Cv2.BoundingRect(outline);
        box = new Rect(box.X - 2, box.Y - 2, box.Width + 4, box.Height + shadowDrop + 4) & new Rect(0, 0, img.Width, img.Height);
        using var roi = new Mat(img, box);
        Point[] local = outline.Select(p => new Point(p.X - box.X, p.Y - box.Y)).ToArray();
        PaintLocal(roi, local, colour, shadowDrop);
    }

    /// <summary>Intersection over union of two filled polygons on a canvas.</summary>
    public static double IoU(Point[] a, Point[] b, Size canvas)
    {
        using var ma = new Mat(canvas, MatType.CV_8UC1, Scalar.All(0));
        using var mb = new Mat(canvas, MatType.CV_8UC1, Scalar.All(0));
        Cv2.FillPoly(ma, new[] { a }, Scalar.All(255));
        Cv2.FillPoly(mb, new[] { b }, Scalar.All(255));
        using var inter = new Mat();
        using var union = new Mat();
        Cv2.BitwiseAnd(ma, mb, inter);
        Cv2.BitwiseOr(ma, mb, union);
        return (double)Cv2.CountNonZero(inter) / Math.Max(1, Cv2.CountNonZero(union));
    }

    private static void PaintLocal(Mat img, Point[] outline, Scalar colour, int shadowDrop)
    {
        if (shadowDrop > 0)
        {
            Point[] shadow = outline.Select(p => new Point(p.X, p.Y + shadowDrop)).ToArray();
            using var shadowMask = new Mat(img.Size(), MatType.CV_8UC1, Scalar.All(0));
            Cv2.FillPoly(shadowMask, new[] { shadow }, Scalar.All(255));
            using var dark = new Mat();
            img.ConvertTo(dark, -1, 0.65, 0);
            dark.CopyTo(img, shadowMask);
        }

        using var holdMask = new Mat(img.Size(), MatType.CV_8UC1, Scalar.All(0));
        Cv2.FillPoly(holdMask, new[] { outline }, Scalar.All(255));
        using var paintF = new Mat(img.Size(), MatType.CV_32FC3, colour);
        using var texture = new Mat(img.Size(), MatType.CV_32FC3);
        Cv2.Randn(texture, new Scalar(0, 0, 0), new Scalar(6, 6, 6));
        Cv2.Add(paintF, texture, paintF);
        using var paint = new Mat();
        paintF.ConvertTo(paint, MatType.CV_8UC3);
        paint.CopyTo(img, holdMask);
    }
}
