using OpenCvSharp;

namespace Blocwerk.HoldDetection.Outlines;

/// <summary>
/// Second-choice segmentation for when the colour pass leaked into a touching neighbour or broke apart:
/// GrabCut, initialised from the seed — definite background beyond 1.35 seed radii, probable foreground
/// inside the seed ellipse, definite foreground in the core. Because everything past 1.35 r is fixed
/// background it cannot bleed far; the size checks still decide whether its answer is believable.
/// Runs at a reduced resolution (seed radius ≤ <see cref="MaxRadius"/> px) to stay within the time budget.
/// </summary>
internal static class GrabCutSegmenter
{
    /// <summary>Largest seed radius, in pixels, GrabCut is run at.</summary>
    public const double MaxRadius = 32;

    /// <summary>Segments and returns a mask at the crop's working resolution (caller disposes), or null.</summary>
    /// <param name="crop">The working crop.</param>
    /// <returns>A binary mask the size of <see cref="OutlineCrop.Bgr"/>, or null when GrabCut fails.</returns>
    public static Mat? Segment(OutlineCrop crop)
    {
        double s = Math.Min(1.0, MaxRadius / crop.Radius);
        using var small = new Mat();
        if (s < 1.0)
        {
            Cv2.Resize(crop.Bgr, small, new Size(0, 0), s, s, InterpolationFlags.Area);
        }
        else
        {
            crop.Bgr.CopyTo(small);
        }

        using var gc = InitialMask(small, crop, (double)small.Width / crop.Bgr.Width);
        using var bgModel = new Mat();
        using var fgModel = new Mat();
        try
        {
            Cv2.GrabCut(small, gc, new Rect(0, 0, small.Width, small.Height), bgModel, fgModel, 3, GrabCutModes.InitWithMask);
        }
        catch (OpenCVException)
        {
            return null;
        }

        using var fg = new Mat();
        using var prFg = new Mat();
        Cv2.Compare(gc, new Scalar((int)GrabCutClasses.FGD), fg, CmpType.EQ);
        Cv2.Compare(gc, new Scalar((int)GrabCutClasses.PR_FGD), prFg, CmpType.EQ);
        using var any = new Mat();
        Cv2.BitwiseOr(fg, prFg, any);
        var full = new Mat();
        Cv2.Resize(any, full, crop.Bgr.Size(), 0, 0, InterpolationFlags.Linear);
        Cv2.Threshold(full, full, 127, 255, ThresholdTypes.Binary);
        return full;
    }

    private static Mat InitialMask(Mat small, OutlineCrop crop, double s)
    {
        var mask = new Mat(small.Rows, small.Cols, MatType.CV_8UC1, new Scalar((int)GrabCutClasses.BGD));
        var centre = new Point((int)Math.Round(crop.Centre.X * s), (int)Math.Round(crop.Centre.Y * s));
        Size Axes(double k) => new(
            Math.Max(1, (int)Math.Round(crop.RadiusX * s * k)),
            Math.Max(1, (int)Math.Round(crop.RadiusY * s * k)));

        Cv2.Ellipse(mask, centre, Axes(1.35), 0, 0, 360, new Scalar((int)GrabCutClasses.PR_BGD), -1);
        Cv2.Ellipse(mask, centre, Axes(0.95), 0, 0, 360, new Scalar((int)GrabCutClasses.PR_FGD), -1);
        Cv2.Ellipse(mask, centre, Axes(0.25), 0, 0, 360, new Scalar((int)GrabCutClasses.FGD), -1);
        return mask;
    }
}
