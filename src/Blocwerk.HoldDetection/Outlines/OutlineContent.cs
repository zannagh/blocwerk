// <copyright file="OutlineContent.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using OpenCvSharp;

namespace Blocwerk.HoldDetection.Outlines;

/// <summary>
/// What lies inside an outline, read off the photo. Two things make an outline one to look at instead of one to accept
/// unseen:
/// <list type="bullet">
/// <item>a part below the hold's middle that is clearly darker than its upper part: the shadow under the hold, which
/// hangs off its lower side (<see cref="MaxDarkLowerShare"/>);</item>
/// <item>a part whose colour is far from the hold's own: wall or wood taken in where the outline bridges a hollow, or
/// a piece of a neighbour (<see cref="MaxForeignShare"/>).</item>
/// </list>
/// A hold of two colours, or one with a large bolt hole, fails these too. That only costs it the bulk step.
/// </summary>
internal static class OutlineContent
{
    /// <summary>The share of an outline that may be dark and on its lower side.</summary>
    public const double MaxDarkLowerShare = 0.08;

    /// <summary>The share of an outline that may be of another colour than the hold.</summary>
    public const double MaxForeignShare = 0.08;

    /// <summary>A pixel is dark when its lightness (0..100) is this much under the median of the hold's upper part.</summary>
    public const double DarkStep = 18;

    /// <summary>A pixel is of another colour from this distance (Lab) to the median colour inside the outline.</summary>
    public const double ForeignDistance = 28;

    private const int MinPixels = 30;

    /// <summary>True when the outline takes in neither a shadow under the hold nor a clear patch of another colour.</summary>
    /// <param name="image">The full BGR photo.</param>
    /// <param name="seed">The hold's seed.</param>
    /// <param name="result">The outline.</param>
    /// <returns>Whether the inside of the outline looks like one hold.</returns>
    public static bool IsOneHold(Mat image, HoldSeed seed, HoldOutlineResult result)
    {
        var (darkLower, foreign) = Measure(image, seed, result);
        return darkLower < MaxDarkLowerShare && foreign < MaxForeignShare;
    }

    /// <summary>The share of the outline that is dark and on its lower side, and the share that is of another colour.</summary>
    /// <param name="image">The full BGR photo.</param>
    /// <param name="seed">The hold's seed.</param>
    /// <param name="result">The outline.</param>
    /// <returns>Both shares, 0..1; zero when the outline is too small to tell.</returns>
    public static (double DarkLower, double Foreign) Measure(Mat image, HoldSeed seed, HoldOutlineResult result)
    {
        using OutlineCrop? crop = OutlineCrop.Create(image, seed);
        if (crop is null)
        {
            return (0, 0);
        }

        Point[] polygon = [.. result.Polygon.Select(p => new Point(
            (int)Math.Round(((p.X * image.Width) - crop.Roi.X) * crop.Scale),
            (int)Math.Round(((p.Y * image.Height) - crop.Roi.Y) * crop.Scale)))];
        int width = crop.Bgr.Width, height = crop.Bgr.Height;
        using Mat mask = MaskOps.Fill(polygon, width, height);
        using var lab = new Mat();
        Cv2.CvtColor(crop.Bgr, lab, ColorConversionCodes.BGR2Lab);
        byte[] inside = CropPixels.ToBytes(mask), colours = CropPixels.ToBytes(lab);
        var pixels = new List<(int Y, double L, double A, double B)>();
        for (int i = 0; i < inside.Length; i++)
        {
            if (inside[i] != 0)
            {
                pixels.Add((i / width, colours[i * 3] * 100.0 / 255, colours[(i * 3) + 1] - 128.0, colours[(i * 3) + 2] - 128.0));
            }
        }

        if (pixels.Count < MinPixels)
        {
            return (0, 0);
        }

        double middle = pixels.Average(p => p.Y);
        var upper = pixels.Where(p => p.Y < middle).Select(p => p.L).ToList();
        if (upper.Count == 0)
        {
            return (0, 0);
        }

        double upperLight = Median(upper);
        double l = Median([.. pixels.Select(p => p.L)]), a = Median([.. pixels.Select(p => p.A)]), b = Median([.. pixels.Select(p => p.B)]);
        int dark = pixels.Count(p => p.Y > middle && p.L < upperLight - DarkStep);
        int foreign = pixels.Count(p => Math.Sqrt(((p.L - l) * (p.L - l)) + ((p.A - a) * (p.A - a)) + ((p.B - b) * (p.B - b))) > ForeignDistance);
        return ((double)dark / pixels.Count, (double)foreign / pixels.Count);
    }

    private static double Median(List<double> values)
    {
        values.Sort();
        return values[values.Count / 2];
    }
}
