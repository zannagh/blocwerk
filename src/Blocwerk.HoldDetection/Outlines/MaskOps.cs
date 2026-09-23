using OpenCvSharp;

namespace Blocwerk.HoldDetection.Outlines;

/// <summary>
/// Mask clean-up shared by both segmenters: morphology, picking the component that holds the seed, hole
/// filling, and the leak/size sanity checks that decide whether an outline is believable.
/// </summary>
internal static class MaskOps
{
    /// <summary>A component larger than this many seed areas is a leak (neighbour or wall bleed).</summary>
    public const double MaxAreaRatio = 1.45;

    /// <summary>A component smaller than this many seed areas is a fragment (chalk patch, bolt hole) when the
    /// seed is only a circle (an elongated crimp can fill little of its circumscribed circle).</summary>
    public const double MinAreaRatio = 0.12;

    /// <summary>The same limit for a seed with a detector box: boxes are tight, real holds fill ≥ 0.5 of the
    /// box's inscribed ellipse, so a small fragment (a hold bolted onto a volume) is rejected.</summary>
    public const double MinAreaRatioBoxed = 0.3;

    /// <summary>No outline vertex may lie further than this many seed radii (ellipse units) from the centre;
    /// the crop edge is at 2, so anything running out towards it is a bleed into the wall or a neighbour.</summary>
    public const double MaxExtent = 1.8;

    /// <summary>Opens (drop specks) then closes (bridge chalk/texture gaps) in place.</summary>
    /// <param name="mask">The binary mask.</param>
    /// <param name="radius">Seed radius in working pixels.</param>
    public static void Clean(Mat mask, double radius)
    {
        int open = Math.Max(3, (int)(radius / 14) | 1);
        int close = Math.Max(3, (int)(radius / 5) | 1);
        using var ko = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(open, open));
        using var kc = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(close, close));
        Cv2.MorphologyEx(mask, mask, MorphTypes.Open, ko);
        Cv2.MorphologyEx(mask, mask, MorphTypes.Close, kc);
    }

    /// <summary>
    /// Returns the filled outer contour of the component containing the seed centre — or, when the centre
    /// itself is background (a bolt hole), of the component covering most of the seed core. Null when no
    /// component touches the core.
    /// </summary>
    /// <param name="mask">The cleaned binary mask.</param>
    /// <param name="crop">The crop (seed geometry).</param>
    /// <returns>The chosen contour, or null.</returns>
    public static Point[]? SeedContour(Mat mask, OutlineCrop crop)
    {
        Cv2.FindContours(mask, out Point[][] contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        Point[]? best = null;
        double bestScore = 0;
        var centre = new Point2f((float)crop.Centre.X, (float)crop.Centre.Y);
        foreach (var contour in contours)
        {
            if (contour.Length < 3)
            {
                continue;
            }

            double signed = Cv2.PointPolygonTest(contour, centre, true);
            double score = signed >= 0 ? 1e9 + Cv2.ContourArea(contour) : CoreOverlap(contour, crop);
            if (score > bestScore)
            {
                bestScore = score;
                best = contour;
            }
        }

        return best;
    }

    /// <summary>Checks a candidate outline against the seed: size window and leak beyond the seed.</summary>
    /// <param name="contour">The candidate contour (working pixels).</param>
    /// <param name="crop">The crop.</param>
    /// <param name="minRatio">Smallest acceptable area in seed areas.</param>
    /// <returns>The verdict and the area ratio.</returns>
    public static (bool Ok, double AreaRatio, string Reason) Assess(Point[] contour, OutlineCrop crop, double minRatio)
    {
        double ratio = Cv2.ContourArea(contour) / crop.SeedArea;
        if (ratio < minRatio)
        {
            return (false, ratio, "too-small");
        }

        if (ratio > MaxAreaRatio)
        {
            return (false, ratio, "leak-area");
        }

        double extent = contour.Max(p => LocalColourModel.Normalized(crop, p.X, p.Y));
        if (extent > MaxExtent)
        {
            return (false, ratio, "leak-extent");
        }

        return (true, ratio, "ok");
    }

    /// <summary>Fills a contour into a fresh mask the size of the crop.</summary>
    /// <param name="contour">The contour.</param>
    /// <param name="width">Mask width.</param>
    /// <param name="height">Mask height.</param>
    /// <returns>The filled mask (caller disposes).</returns>
    public static Mat Fill(Point[] contour, int width, int height)
    {
        var filled = new Mat(height, width, MatType.CV_8UC1, Scalar.All(0));
        Cv2.DrawContours(filled, new[] { contour }, 0, Scalar.All(255), -1);
        return filled;
    }

    /// <summary>Simplifies a contour to at most <paramref name="maxPoints"/> vertices.</summary>
    /// <param name="contour">The dense contour.</param>
    /// <param name="maxPoints">Vertex budget.</param>
    /// <returns>The simplified polygon: 3..<paramref name="maxPoints"/> vertices (the min-area rectangle
    /// when the contour collapses below a triangle).</returns>
    public static Point[] Simplify(Point[] contour, int maxPoints = 24)
    {
        double perimeter = Cv2.ArcLength(contour, true);
        double eps = Math.Max(0.5, perimeter * 0.006);
        Point[] poly = Cv2.ApproxPolyDP(contour, eps, true);
        while (poly.Length > maxPoints)
        {
            eps *= 1.3;
            poly = Cv2.ApproxPolyDP(contour, eps, true);
        }

        if (poly.Length >= 3)
        {
            return poly;
        }

        return Cv2.MinAreaRect(contour).Points().Select(p => new Point((int)Math.Round(p.X), (int)Math.Round(p.Y))).ToArray();
    }

    /// <summary>
    /// How strongly a contour follows image edges: mean Sobel magnitude on the contour divided by the median
    /// magnitude over the crop. ~1 on texture or a flat surface, well above 2 on a real object boundary.
    /// </summary>
    /// <param name="contour">The contour (working pixels).</param>
    /// <param name="crop">The crop.</param>
    /// <returns>The ratio.</returns>
    public static double EdgeSupport(Point[] contour, OutlineCrop crop)
    {
        using var grey = new Mat();
        Cv2.CvtColor(crop.Bgr, grey, ColorConversionCodes.BGR2GRAY);
        using var gx = new Mat();
        using var gy = new Mat();
        Cv2.Sobel(grey, gx, MatType.CV_32F, 1, 0, 3);
        Cv2.Sobel(grey, gy, MatType.CV_32F, 0, 1, 3);
        using var mag = new Mat();
        Cv2.Magnitude(gx, gy, mag);
        mag.GetArray(out float[] values);
        var sorted = (float[])values.Clone();
        Array.Sort(sorted);
        double median = Math.Max(1.0, sorted[sorted.Length / 2]);
        double sum = 0;
        int n = 0;
        foreach (var p in contour)
        {
            // Best response within one pixel: a traced boundary sits on, not exactly at, the gradient peak.
            float best = 0;
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    int x = Math.Clamp(p.X + dx, 0, mag.Width - 1), y = Math.Clamp(p.Y + dy, 0, mag.Height - 1);
                    best = Math.Max(best, values[(y * mag.Width) + x]);
                }
            }

            sum += best;
            n++;
        }

        return n == 0 ? 0 : sum / n / median;
    }

    /// <summary>Intersection over union of a contour with the seed's own ellipse.</summary>
    /// <param name="contour">The contour (working pixels).</param>
    /// <param name="crop">The crop.</param>
    /// <returns>0..1.</returns>
    public static double SeedEllipseIoU(Point[] contour, OutlineCrop crop)
    {
        using Mat a = Fill(contour, crop.Bgr.Width, crop.Bgr.Height);
        using var b = new Mat(crop.Bgr.Height, crop.Bgr.Width, MatType.CV_8UC1, Scalar.All(0));
        Cv2.Ellipse(b, new Point((int)crop.Centre.X, (int)crop.Centre.Y), new Size((int)crop.RadiusX, (int)crop.RadiusY), 0, 0, 360, Scalar.All(255), -1);
        using var inter = new Mat();
        using var union = new Mat();
        Cv2.BitwiseAnd(a, b, inter);
        Cv2.BitwiseOr(a, b, union);
        int u = Cv2.CountNonZero(union);
        return u == 0 ? 0 : (double)Cv2.CountNonZero(inter) / u;
    }

    private static double CoreOverlap(Point[] contour, OutlineCrop crop)
    {
        int hits = 0;
        double r = crop.Radius * 0.35;
        for (int k = 0; k < 16; k++)
        {
            double ang = k * Math.PI / 8;
            var p = new Point2f((float)(crop.Centre.X + (r * Math.Cos(ang))), (float)(crop.Centre.Y + (r * Math.Sin(ang))));
            if (Cv2.PointPolygonTest(contour, p, false) >= 0)
            {
                hits++;
            }
        }

        return hits >= 4 ? hits : 0;
    }
}
