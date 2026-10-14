// <copyright file="MaskPlausibility.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using OpenCvSharp;

namespace Blocwerk.HoldDetection.Segmentation;

/// <summary>
/// Makes a segmented hold physically believable: a hold is a solid moulded body, so it has no thin tendrils or
/// spikes sticking out (they would break off) and no narrow slits cut deep into it. Parts thinner than a share of the
/// hold's own size are taken off, slits narrower than that are closed. The outliner has no millimetres, so "thin" is
/// relative: <see cref="ThinShare"/> of the diameter of the circle with the hold's area, which for a 10 cm hold is
/// about 1.3 cm. A hold that is thin as a whole (a rail, an edge) is left as it is.
/// </summary>
internal static class MaskPlausibility
{
    /// <summary>A part thinner than this share of the hold's equivalent diameter is not part of the hold.</summary>
    public const double ThinShare = 0.13;

    /// <summary>...but never more than this share of the hold's short side, so a rail keeps its ends.</summary>
    public const double ShortSideShare = 0.45;

    /// <summary>Taking the thin parts off may cost at most this share of the area; beyond it the hold itself is thin.</summary>
    public const double MaxRemovedShare = 0.3;

    /// <summary>Closing the slits may add at most this share of the area; beyond it the shape is really concave.</summary>
    public const double MaxFilledShare = 0.2;

    /// <summary>A bite out of the outline (a notch, a step at a corner) is filled when it is at most this share of the hold's area.</summary>
    public const double MaxNotchShare = 0.04;

    /// <summary>Cleans a binary mask (0/255) in place.</summary>
    /// <param name="mask">The mask; holds one object, possibly with loose specks.</param>
    public static void Clean(Mat mask)
    {
        int kernel = KernelFor(mask);
        if (kernel < 3)
        {
            return;
        }

        using var element = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(kernel, kernel));
        int area = Cv2.CountNonZero(mask);
        using var opened = new Mat();
        Cv2.MorphologyEx(mask, opened, MorphTypes.Open, element);
        if (Cv2.CountNonZero(opened) >= (1 - MaxRemovedShare) * area)
        {
            opened.CopyTo(mask);
            area = Cv2.CountNonZero(mask);
        }

        using var closed = new Mat();
        Cv2.MorphologyEx(mask, closed, MorphTypes.Close, element);
        if (Cv2.CountNonZero(closed) <= (1 + MaxFilledShare) * area)
        {
            closed.CopyTo(mask);
        }

        FillNotches(mask);
    }

    /// <summary>
    /// Fills the small bites between the outline and its convex hull: a notch or a step cut into an edge is not how a
    /// hold is moulded, it is where a shadow, chalk or a neighbour's edge misled the mask. A bay that is a real part of
    /// the shape is larger than <see cref="MaxNotchShare"/> of the hold and stays.
    /// </summary>
    /// <param name="mask">The mask; holds one object.</param>
    public static void FillNotches(Mat mask)
    {
        Cv2.FindContours(mask, out Point[][] contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        if (contours.Length == 0)
        {
            return;
        }

        Point[] largest = contours.MaxBy(c => Cv2.ContourArea(c))!;
        double area = Cv2.ContourArea(largest);
        if (area < 16 || largest.Length < 3)
        {
            return;
        }

        using var hull = new Mat(mask.Rows, mask.Cols, MatType.CV_8UC1, Scalar.All(0));
        Cv2.FillConvexPoly(hull, Cv2.ConvexHull(largest), Scalar.All(255));
        using var bites = new Mat();
        using var outside = new Mat();
        Cv2.BitwiseNot(mask, outside);
        Cv2.BitwiseAnd(hull, outside, bites);
        Cv2.FindContours(bites, out Point[][] gaps, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        var small = gaps.Where(g => g.Length >= 3 && Cv2.ContourArea(g) <= MaxNotchShare * area).ToArray();
        if (small.Length > 0)
        {
            Cv2.DrawContours(mask, small, -1, Scalar.All(255), -1);
        }
    }

    /// <summary>The odd kernel size, in mask pixels, below which a part counts as thin; 0 for an empty mask.</summary>
    /// <param name="mask">The mask.</param>
    /// <returns>The kernel size.</returns>
    public static int KernelFor(Mat mask)
    {
        Cv2.FindContours(mask, out Point[][] contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        if (contours.Length == 0)
        {
            return 0;
        }

        Point[] largest = contours.MaxBy(c => Cv2.ContourArea(c))!;
        double area = Cv2.ContourArea(largest);
        if (area < 16 || largest.Length < 3)
        {
            return 0;
        }

        var box = Cv2.MinAreaRect(largest);
        double shortSide = Math.Min(box.Size.Width, box.Size.Height);
        double size = Math.Min(ThinShare * 2 * Math.Sqrt(area / Math.PI), ShortSideShare * shortSide);
        return (int)Math.Round(size) | 1;
    }
}
