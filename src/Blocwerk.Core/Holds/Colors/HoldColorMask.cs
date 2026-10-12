// <copyright file="HoldColorMask.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Holds.Colors;

/// <summary>
/// Which photo pixels belong to a hold for the colour estimate, and which to the ring of surroundings around it. A
/// traced outline is pulled in a little (its rim is shadow and wall); a hold that is only a circle uses the circle's
/// core, because the circle encloses the hold and most of its rim is wall. Pocket holes are left out. Pure geometry in
/// photo pixels.
/// </summary>
internal sealed class HoldColorMask
{
    /// <summary>The bounding box is sampled on a grid of at most this many steps per side.</summary>
    private const int MaxSteps = 96;

    private const double CircleCore = 0.55;
    private const double OutlineCore = 0.8;
    private const double RingFrom = 1.3;
    private const double RingTo = 1.9;

    private readonly double centreX;
    private readonly double centreY;
    private readonly double radius;
    private readonly (double X, double Y)[]? outline;
    private readonly (double X, double Y)[][] holes;

    private HoldColorMask(double centreX, double centreY, double radius, (double X, double Y)[]? outline, (double X, double Y)[][] holes)
    {
        this.centreX = centreX;
        this.centreY = centreY;
        this.radius = radius;
        this.outline = outline;
        this.holes = holes;
    }

    /// <summary>The distance between two sampled pixels, so a large hold costs no more than a small one.</summary>
    public int Stride => Math.Max(1, (int)Math.Ceiling(2 * RingTo * radius / MaxSteps));

    /// <summary>The mask of a hold on a photo of the given size.</summary>
    /// <param name="hold">The hold; position and shape are fractions of the photo.</param>
    /// <param name="width">Photo width in pixels.</param>
    /// <param name="height">Photo height in pixels.</param>
    /// <returns>The mask.</returns>
    public static HoldColorMask For(Hold hold, int width, int height)
    {
        (double X, double Y)[] ToPixels(IEnumerable<ShapePoint> ring) =>
            ring.Select(p => ((hold.X + p.Dx) * width, (hold.Y + p.Dy) * height)).ToArray();

        // Radius is a fraction of the photo's long side (the detector's convention).
        var radius = Math.Max(1.0, hold.Radius * Math.Max(width, height));
        if (hold.ShapePoints is not { Count: >= 3 })
        {
            return new HoldColorMask(hold.X * width, hold.Y * height, radius, null, []);
        }

        var outline = ToPixels(hold.ShapePoints);
        double cx = outline.Average(p => p.X), cy = outline.Average(p => p.Y);
        var reach = outline.Max(p => Math.Sqrt(((p.X - cx) * (p.X - cx)) + ((p.Y - cy) * (p.Y - cy))));
        var holes = (hold.ShapeHoles ?? []).Where(h => h.Count >= 3).Select(ToPixels).ToArray();
        return new HoldColorMask(cx, cy, Math.Max(1.0, reach), outline, holes);
    }

    /// <summary>The pixel box that holds the mask and its ring, clipped to the photo (right and bottom exclusive).</summary>
    /// <param name="width">Photo width in pixels.</param>
    /// <param name="height">Photo height in pixels.</param>
    /// <returns>The box.</returns>
    public (int Left, int Top, int Right, int Bottom) Bounds(int width, int height)
    {
        var reach = RingTo * radius;
        return (
            Math.Max(0, (int)Math.Floor(centreX - reach)),
            Math.Max(0, (int)Math.Floor(centreY - reach)),
            Math.Min(width, (int)Math.Ceiling(centreX + reach) + 1),
            Math.Min(height, (int)Math.Ceiling(centreY + reach) + 1));
    }

    /// <summary>Whether the pixel counts as the hold.</summary>
    /// <param name="x">Pixel centre x.</param>
    /// <param name="y">Pixel centre y.</param>
    /// <returns>True inside the hold's core.</returns>
    public bool IsHold(double x, double y)
    {
        if (outline is null)
        {
            return Distance(x, y) <= CircleCore * radius;
        }

        return InsideScaled(x, y, OutlineCore) && !holes.Any(h => Inside(h, x, y));
    }

    /// <summary>Whether the pixel counts as the hold's surroundings.</summary>
    /// <param name="x">Pixel centre x.</param>
    /// <param name="y">Pixel centre y.</param>
    /// <returns>True in the ring around the hold.</returns>
    public bool IsSurroundings(double x, double y)
    {
        if (outline is null)
        {
            var distance = Distance(x, y);
            return distance >= RingFrom * radius && distance <= RingTo * radius;
        }

        return !InsideScaled(x, y, RingFrom) && InsideScaled(x, y, RingTo);
    }

    private static bool Inside((double X, double Y)[] ring, double x, double y)
    {
        var inside = false;
        for (int i = 0, j = ring.Length - 1; i < ring.Length; j = i++)
        {
            var (xi, yi) = ring[i];
            var (xj, yj) = ring[j];
            if ((yi > y) != (yj > y) && x < ((xj - xi) * (y - yi) / (yj - yi)) + xi)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    private double Distance(double x, double y) => Math.Sqrt(((x - centreX) * (x - centreX)) + ((y - centreY) * (y - centreY)));

    // The outline grown or shrunk about its centre by `scale`: the point is tested in the outline's own scale.
    private bool InsideScaled(double x, double y, double scale) =>
        Inside(outline!, centreX + ((x - centreX) / scale), centreY + ((y - centreY) / scale));
}
