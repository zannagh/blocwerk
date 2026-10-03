using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Detection.Outlines;
using OpenCvSharp;

namespace Blocwerk.HoldDetection.Outlines;

/// <summary>Turns a working-resolution contour (or the seed circle) into a <see cref="HoldOutlineResult"/>.</summary>
internal static class OutlineResultBuilder
{
    /// <summary>Vertex budget of an outline polygon.</summary>
    public const int MaxVertices = 24;

    /// <summary>Builds the result for an accepted contour.</summary>
    /// <param name="crop">The working crop.</param>
    /// <param name="seed">The seed (its centre anchors the shape points).</param>
    /// <param name="contour">The dense contour in working pixels.</param>
    /// <param name="holes">The outline's qualifying interior holes (simplified, working pixels); may be empty.</param>
    /// <param name="imageWidth">Full image width.</param>
    /// <param name="imageHeight">Full image height.</param>
    /// <param name="confidence">The confidence.</param>
    /// <param name="method">How the contour was obtained.</param>
    /// <returns>The result.</returns>
    public static HoldOutlineResult FromContour(
        OutlineCrop crop,
        HoldSeed seed,
        Point[] contour,
        IReadOnlyList<Point[]> holes,
        int imageWidth,
        int imageHeight,
        double confidence,
        HoldOutlineMethod method)
    {
        // The fingerprint is measured on the FILLED outline on purpose: whether a pocket's hole is detected
        // varies from photo to photo (lighting decides if its floor reads as shadow), and re-recognition must
        // see the same hold either way. Only the reported area subtracts the holes.
        using Mat mask = MaskOps.Fill(contour, crop.Bgr.Width, crop.Bgr.Height);
        HoldFingerprint fp = FingerprintExtractor.Extract(crop, mask, contour);
        Point[] poly = MaskOps.Simplify(contour, MaxVertices);
        var polygon = poly.Select(p => crop.ToNormalized(p.X, p.Y, imageWidth, imageHeight)).ToList();
        double holeAreaPx = holes.Sum(h => Cv2.ContourArea(h)) / (crop.Scale * crop.Scale);
        var result = Build(polygon, seed, Math.Max(0, fp.AreaPx - holeAreaPx), confidence, method, fp, withShape: true);
        if (holes.Count == 0)
        {
            return result;
        }

        var rings = holes
            .Select(h => HoldOutlineGeometry.ToShapePoints(h.Select(p => crop.ToNormalized(p.X, p.Y, imageWidth, imageHeight)), seed.X, seed.Y))
            .ToList();
        return result with { ShapeHoles = rings };
    }

    /// <summary>
    /// Builds the circle fallback: the seed circle (a true circle in pixels, 16 vertices) with a
    /// fingerprint measured on the inner 0.8 of the seed disc, confidence ≤ 0.2, and no shape points.
    /// </summary>
    /// <param name="crop">The working crop.</param>
    /// <param name="seed">The seed.</param>
    /// <param name="imageWidth">Full image width.</param>
    /// <param name="imageHeight">Full image height.</param>
    /// <param name="contrast01">The hold/wall contrast score 0..1 (0 for a wall-coloured volume).</param>
    /// <returns>The result.</returns>
    public static HoldOutlineResult Circle(OutlineCrop crop, HoldSeed seed, int imageWidth, int imageHeight, double contrast01)
    {
        var disc = new Point[16];
        for (int k = 0; k < disc.Length; k++)
        {
            double ang = k * Math.PI / 8;
            disc[k] = new Point(
                (int)Math.Round(crop.Centre.X + (crop.RadiusX * 0.8 * Math.Cos(ang))),
                (int)Math.Round(crop.Centre.Y + (crop.RadiusY * 0.8 * Math.Sin(ang))));
        }

        using Mat mask = MaskOps.Fill(disc, crop.Bgr.Width, crop.Bgr.Height);
        HoldFingerprint fp = FingerprintExtractor.Extract(crop, mask, disc);
        var polygon = Enumerable.Range(0, 16)
            .Select(k =>
            {
                double ang = k * Math.PI / 8;
                return crop.ToNormalized(
                    crop.Centre.X + (crop.RadiusX * Math.Cos(ang)),
                    crop.Centre.Y + (crop.RadiusY * Math.Sin(ang)),
                    imageWidth,
                    imageHeight);
            })
            .ToList();
        double area = Math.PI * crop.RadiusX * crop.RadiusY / (crop.Scale * crop.Scale);
        double confidence = Math.Round(0.05 + (0.15 * Math.Clamp(contrast01, 0, 1)), 3);
        return Build(polygon, seed, area, confidence, HoldOutlineMethod.CircleFallback, fp, withShape: false);
    }

    /// <summary>A seed that lies off the image: an empty, zero-confidence fallback.</summary>
    /// <param name="seed">The seed.</param>
    /// <returns>The result.</returns>
    public static HoldOutlineResult Degenerate(HoldSeed seed)
    {
        return new HoldOutlineResult(
            [new NormalizedPoint(seed.X, seed.Y)],
            seed.X,
            seed.Y,
            null,
            0,
            new HoldOutlineBounds(seed.X, seed.Y, 0, 0),
            0,
            HoldOutlineMethod.CircleFallback,
            new HoldFingerprint());
    }

    private static HoldOutlineResult Build(
        List<NormalizedPoint> polygon,
        HoldSeed seed,
        double areaPx,
        double confidence,
        HoldOutlineMethod method,
        HoldFingerprint fp,
        bool withShape)
    {
        double minX = polygon.Min(p => p.X), maxX = polygon.Max(p => p.X);
        double minY = polygon.Min(p => p.Y), maxY = polygon.Max(p => p.Y);
        return new HoldOutlineResult(
            polygon,
            seed.X,
            seed.Y,
            withShape ? HoldOutlineGeometry.ToShapePoints(polygon, seed.X, seed.Y) : null,
            areaPx,
            new HoldOutlineBounds(minX, minY, maxX - minX, maxY - minY),
            confidence,
            method,
            fp);
    }
}
