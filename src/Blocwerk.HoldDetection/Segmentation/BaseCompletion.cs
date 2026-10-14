// <copyright file="BaseCompletion.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.HoldDetection.Outlines;
using OpenCvSharp;

namespace Blocwerk.HoldDetection.Segmentation;

/// <summary>
/// A large hold with holds screwed onto it: the model outlines the base without the holds on it, which cuts the base's
/// silhouette where a hold sits on its edge (the tip under a hold is lost). The base's outline is completed with the
/// outlines of the holds that lie on it and touch it, so it runs under them. The holds on it keep their own outlines.
/// </summary>
internal static class BaseCompletion
{
    /// <summary>The base's outline grown by this many working pixels is where a hold on it may lie.</summary>
    public const int TouchPx = 3;

    /// <summary>A hold lies on the base when at least this share of it is inside the grown outline; a neighbour that only touches it is not.</summary>
    public const double MinShareOnBase = 0.25;

    /// <summary>
    /// A base is completed only with at least this many holds on it. With one, it is as often a hold beside a large one
    /// as on it, and completing a large hold with a neighbour pushed it out of shape in the clean-up.
    /// </summary>
    public const int MinHoldsOnIt = 2;

    /// <summary>The median colourfulness (Lab chroma, 0..~128) a base needs: a hold cast in a colour, not bare wood or white.</summary>
    public const double MinChroma = 25;

    /// <summary>Completes the outlines of the bases among the boxed seeds, in place.</summary>
    /// <param name="image">The full BGR photo.</param>
    /// <param name="seeds">Every seed.</param>
    /// <param name="boxed">Seed index per box.</param>
    /// <param name="boxes">The detector boxes, in full-image pixels.</param>
    /// <param name="results">The outlines per seed; bases are replaced.</param>
    public static void Complete(Mat image, IReadOnlyList<HoldSeed> seeds, IReadOnlyList<int> boxed, IReadOnlyList<SegmentBox> boxes, HoldOutlineResult?[] results)
    {
        var completed = new Dictionary<int, HoldOutlineResult>();
        for (int m = 0; m < boxed.Count; m++)
        {
            var kidIndexes = BaseChildren.Of(boxes, m);
            var kids = kidIndexes.Select(k => results[boxed[k]]).OfType<HoldOutlineResult>().ToList();
            var others = Enumerable.Range(0, boxes.Count).Where(o => o != m && !kidIndexes.Contains(o))
                .Select(o => new Point2d((boxes[o].Left + boxes[o].Right) / 2, (boxes[o].Top + boxes[o].Bottom) / 2)).ToList();
            if (kids.Count >= MinHoldsOnIt && results[boxed[m]] is { } basis && Completed(image, seeds[boxed[m]], basis, kids, others) is { } done)
            {
                completed[boxed[m]] = done;
            }
        }

        foreach (var (index, result) in completed)
        {
            results[index] = result;
        }
    }

    private static HoldOutlineResult? Completed(
        Mat image, HoldSeed seed, HoldOutlineResult basis, IReadOnlyList<HoldOutlineResult> kids, IReadOnlyList<Point2d> others)
    {
        using OutlineCrop? crop = OutlineCrop.Create(image, seed);
        if (crop is null)
        {
            return null;
        }

        int w = crop.Bgr.Width, h = crop.Bgr.Height;
        using Mat mask = MaskOps.Fill(InCrop(basis, crop, image), w, h);

        // Only a large hold cast in a colour is a base; a wooden or white volume with holds on it is not completed.
        if (Chroma(crop.Bgr, mask) < MinChroma)
        {
            return null;
        }

        using var near = new Mat();
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size((2 * TouchPx) + 1, (2 * TouchPx) + 1));
        Cv2.Dilate(mask, near, kernel);
        bool grew = false;
        foreach (var kid in kids)
        {
            using Mat kidMask = MaskOps.Fill(InCrop(kid, crop, image), w, h);
            using var shared = new Mat();
            Cv2.BitwiseAnd(kidMask, near, shared);
            if (Cv2.CountNonZero(shared) >= MinShareOnBase * Math.Max(1, Cv2.CountNonZero(kidMask)))
            {
                Cv2.BitwiseOr(mask, kidMask, mask);
                grew = true;
            }
        }

        if (!grew)
        {
            return null;
        }

        using var close = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(7, 7));
        Cv2.MorphologyEx(mask, mask, MorphTypes.Close, close);
        Cv2.FindContours(mask, out Point[][] contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxNone);
        var contour = contours.Where(c => c.Length >= 3).MaxBy(c => Cv2.ContourArea(c));

        // The completed outline must not take in a hold that is not on the base: then the base stays as the model saw it.
        bool takesInAnother = contour is not null && others.Any(o => Cv2.PointPolygonTest(
            contour, new Point2f((float)((o.X - crop.Roi.X) * crop.Scale), (float)((o.Y - crop.Roi.Y) * crop.Scale)), false) > 0);
        return contour is null || takesInAnother
            ? null
            : OutlineResultBuilder.FromContour(crop, seed, contour, [], image.Width, image.Height, basis.Confidence, basis.Method);
    }

    /// <summary>The median colourfulness (Lab chroma) inside the mask.</summary>
    private static double Chroma(Mat bgr, Mat mask)
    {
        using var lab = new Mat();
        Cv2.CvtColor(bgr, lab, ColorConversionCodes.BGR2Lab);
        byte[] colours = CropPixels.ToBytes(lab), inside = CropPixels.ToBytes(mask);
        var chroma = new List<double>();
        for (int i = 0; i < inside.Length; i++)
        {
            if (inside[i] != 0)
            {
                double a = colours[(i * 3) + 1] - 128.0, b = colours[(i * 3) + 2] - 128.0;
                chroma.Add(Math.Sqrt((a * a) + (b * b)));
            }
        }

        if (chroma.Count == 0)
        {
            return 0;
        }

        chroma.Sort();
        return chroma[chroma.Count / 2];
    }

    private static Point[] InCrop(HoldOutlineResult result, OutlineCrop crop, Mat image) =>
        [.. result.Polygon.Select(p => new Point(
            (int)Math.Round(((p.X * image.Width) - crop.Roi.X) * crop.Scale),
            (int)Math.Round(((p.Y * image.Height) - crop.Roi.Y) * crop.Scale)))];
}
