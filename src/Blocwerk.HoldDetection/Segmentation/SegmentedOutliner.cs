// <copyright file="SegmentedOutliner.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Runtime.InteropServices;
using Blocwerk.Core.Abstractions;
using Blocwerk.HoldDetection.Outlines;
using OpenCvSharp;

namespace Blocwerk.HoldDetection.Segmentation;

/// <summary>
/// Outlines holds with the segmentation model: every hold that came with a detector box is cut out as "the object in
/// this box", which keeps two touching holds apart (each has its own box) and finds pale or wooden holds a colour
/// threshold cannot tell from the plywood. The mask is cut to the box (grown by <see cref="BoxGrowth"/>), made
/// believable (<see cref="MaskPlausibility"/>) and has to pass the size checks; a hold that fails gets no answer here
/// and is outlined the old way by the caller.
/// </summary>
internal sealed class SegmentedOutliner
{
    /// <summary>The mask may reach this far beyond the detector box on each side: boxes are tight, sometimes too tight.</summary>
    public const double BoxGrowth = 1.12;

    /// <summary>Smallest believable outline, as a share of the box area.</summary>
    public const double MinBoxShare = 0.1;

    /// <summary>The model's own quality score below which its answer is not used.</summary>
    public const double MinScore = 0.5;

    private const float Outside = -20f;

    private readonly HoldSegmenter segmenter;

    /// <summary>Initializes a new instance of the <see cref="SegmentedOutliner"/> class.</summary>
    /// <param name="segmenter">The model.</param>
    public SegmentedOutliner(HoldSegmenter segmenter)
    {
        this.segmenter = segmenter;
    }

    /// <summary>Outlines every seed that has a box.</summary>
    /// <param name="image">The full BGR photo.</param>
    /// <param name="seeds">The seeds.</param>
    /// <returns>Per seed the outline, or null when it has no box or the model's answer was not believable.</returns>
    public HoldOutlineResult?[] Outline(Mat image, IReadOnlyList<HoldSeed> seeds)
    {
        var results = new HoldOutlineResult?[seeds.Count];
        var boxed = Enumerable.Range(0, seeds.Count).Where(i => seeds[i].BoxWidth is > 0 && seeds[i].BoxHeight is > 0).ToList();
        var boxes = boxed.Select(i => BoxOf(seeds[i], image.Width, image.Height)).ToList();
        foreach (var window in SegmentWindows.Plan(boxes, image.Width, image.Height))
        {
            var rect = new Rect(window.X, window.Y, window.Width, window.Height);
            var (embedding, scale) = segmenter.Embed(image, rect);
            foreach (int member in window.Members)
            {
                var box = boxes[member];
                var prompt = new SegmentBox((box.Left - rect.X) * scale, (box.Top - rect.Y) * scale, (box.Right - rect.X) * scale, (box.Bottom - rect.Y) * scale);
                var (logits, score) = segmenter.Decode(embedding, prompt);
                results[boxed[member]] = score >= MinScore ? FromMask(image, seeds[boxed[member]], logits, rect, scale, score) : null;
            }
        }

        return results;
    }

    private static SegmentBox BoxOf(HoldSeed seed, int width, int height)
    {
        double w = seed.BoxWidth!.Value * width, h = seed.BoxHeight!.Value * height;
        double cx = seed.X * width, cy = seed.Y * height;
        return new SegmentBox(cx - (w / 2), cy - (h / 2), cx + (w / 2), cy + (h / 2));
    }

    private static HoldOutlineResult? FromMask(Mat image, HoldSeed seed, float[] logits, Rect window, double scale, double score)
    {
        using OutlineCrop? crop = OutlineCrop.Create(image, seed);
        if (crop is null)
        {
            return null;
        }

        using Mat mask = MaskInCrop(logits, crop, window, scale);
        ClipToBox(mask, crop);
        MaskPlausibility.Clean(mask);
        Point[]? contour = MaskOps.SeedContour(mask, crop) ?? Largest(mask);
        if (contour is null)
        {
            return null;
        }

        double share = Cv2.ContourArea(contour) / (4 * crop.RadiusX * crop.RadiusY);
        if (share < MinBoxShare)
        {
            return null;
        }

        var px = CropPixels.From(crop.Bgr);
        var model = LocalColourModel.Sample(px, crop);
        IReadOnlyList<Point[]> holes = model is null ? [] : HoleFinder.Find(mask, contour, px, model);
        double confidence = Math.Round(Math.Clamp(score, 0.21, 0.97), 3);
        return OutlineResultBuilder.FromContour(crop, seed, contour, holes, image.Width, image.Height, confidence, HoldOutlineMethod.Segmenter);
    }

    /// <summary>The model's mask resampled into the crop's working pixels and cut at zero.</summary>
    private static Mat MaskInCrop(float[] logits, OutlineCrop crop, Rect window, double scale)
    {
        using var low = new Mat(HoldSegmenter.MaskSize, HoldSegmenter.MaskSize, MatType.CV_32FC1);
        Marshal.Copy(logits, 0, low.Data, logits.Length);

        // A working pixel p lies at full-image pixel roi + p / cropScale, which is mask cell (that - window) * scale / 4.
        double cell = (double)HoldSegmenter.InputSize / HoldSegmenter.MaskSize;
        double a = scale / (cell * crop.Scale);
        double bx = (((crop.Roi.X - window.X) * scale) + 0.5) / cell - 0.5;
        double by = (((crop.Roi.Y - window.Y) * scale) + 0.5) / cell - 0.5;
        using var map = new Mat(2, 3, MatType.CV_64FC1);
        map.SetArray(new[] { a, 0, bx, 0, a, by });
        using var warped = new Mat();
        Cv2.WarpAffine(low, warped, map, crop.Bgr.Size(), InterpolationFlags.Linear | InterpolationFlags.WarpInverseMap, BorderTypes.Constant, new Scalar(Outside));
        using var positive = new Mat();
        Cv2.Threshold(warped, positive, 0, 255, ThresholdTypes.Binary);
        var mask = new Mat();
        positive.ConvertTo(mask, MatType.CV_8UC1);
        return mask;
    }

    private static void ClipToBox(Mat mask, OutlineCrop crop)
    {
        using var allowed = new Mat(mask.Rows, mask.Cols, MatType.CV_8UC1, Scalar.All(0));
        var from = new Point((int)Math.Floor(crop.Centre.X - (crop.RadiusX * BoxGrowth)), (int)Math.Floor(crop.Centre.Y - (crop.RadiusY * BoxGrowth)));
        var to = new Point((int)Math.Ceiling(crop.Centre.X + (crop.RadiusX * BoxGrowth)), (int)Math.Ceiling(crop.Centre.Y + (crop.RadiusY * BoxGrowth)));
        Cv2.Rectangle(allowed, from, to, Scalar.All(255), -1);
        Cv2.BitwiseAnd(mask, allowed, mask);
    }

    private static Point[]? Largest(Mat mask)
    {
        Cv2.FindContours(mask, out Point[][] contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        return contours.Where(c => c.Length >= 3).MaxBy(c => Cv2.ContourArea(c));
    }
}
