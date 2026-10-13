// <copyright file="HoldArea.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;

namespace Blocwerk.Core.LookOver;

/// <summary>
/// The area a hold (or a detection) takes on its photo, in pixels: its traced outline when it has one, else its
/// circle. Positions are normalised per axis and radii to the long side, so everything is brought to pixels first.
/// </summary>
internal sealed class HoldArea
{
    private readonly (double X, double Y)[]? polygon;
    private readonly double centreX;
    private readonly double centreY;
    private readonly double radiusPx;
    private readonly bool mayCarry = true;

    /// <summary>Initializes a new instance of the <see cref="HoldArea"/> class.</summary>
    /// <param name="x">Centre across the photo, 0..1.</param>
    /// <param name="y">Centre down the photo, 0..1.</param>
    /// <param name="radius">Radius, normalised to the photo's long side.</param>
    /// <param name="shape">The outline as offsets from the centre, or null for a circle.</param>
    /// <param name="size">The photo's pixel size; null treats it as square.</param>
    public HoldArea(double x, double y, double radius, IReadOnlyList<ShapePoint>? shape, (int Width, int Height)? size)
    {
        var (width, height) = size is { Width: > 0, Height: > 0 } s ? ((double)s.Width, (double)s.Height) : (1d, 1d);
        (centreX, centreY, radiusPx) = (x * width, y * height, radius * Math.Max(width, height));
        polygon = shape is { Count: >= 3 } ? shape.Select(p => ((x + p.Dx) * width, (y + p.Dy) * height)).ToArray() : null;
        (MinX, MaxX) = polygon is null ? (centreX - radiusPx, centreX + radiusPx) : (polygon.Min(p => p.X), polygon.Max(p => p.X));
        (MinY, MaxY) = polygon is null ? (centreY - radiusPx, centreY + radiusPx) : (polygon.Min(p => p.Y), polygon.Max(p => p.Y));
    }

    /// <summary>The area in square pixels: the outline's own (a concave one too), else the circle's.</summary>
    public double Area => polygon is null ? Math.PI * radiusPx * radiusPx : Math.Abs(SignedArea(polygon));

    /// <summary>
    /// Whether another hold can lie ON this one (<see cref="HoldStacks.HoldStacking.MayCarry"/>): only an area with
    /// an outline, and not when the caller says no.
    /// </summary>
    public bool MayCarry
    {
        get => mayCarry && polygon is not null;
        init => mayCarry = value;
    }

    public double MinX { get; }

    public double MaxX { get; }

    public double MinY { get; }

    public double MaxY { get; }

    /// <summary>Whether the two bounding boxes meet: the cheap test before any point is looked at.</summary>
    /// <param name="other">The other area.</param>
    /// <returns>True when they may overlap.</returns>
    public bool MayTouch(HoldArea other) => MinX <= other.MaxX && other.MinX <= MaxX && MinY <= other.MaxY && other.MinY <= MaxY;

    /// <summary>Whether a pixel lies in the area (even-odd ray casting for an outline).</summary>
    /// <param name="x">Pixel x.</param>
    /// <param name="y">Pixel y.</param>
    /// <returns>True when it does.</returns>
    public bool Contains(double x, double y)
    {
        if (polygon is null)
        {
            var (dx, dy) = (x - centreX, y - centreY);
            return (dx * dx) + (dy * dy) <= radiusPx * radiusPx;
        }

        var inside = false;
        for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
        {
            var (a, b) = (polygon[i], polygon[j]);
            if ((a.Y > y) != (b.Y > y) && x < ((b.X - a.X) * (y - a.Y) / (b.Y - a.Y)) + a.X)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    /// <summary>How much of this area the others cover, 0..1, sampled on a grid over its bounding box.</summary>
    /// <param name="others">The areas that may cover it.</param>
    /// <returns>The covered share; 0 for an area without extent.</returns>
    public double CoveredBy(IEnumerable<HoldArea> others)
    {
        const int Grid = 14;
        var near = others.Where(MayTouch).ToList();
        if (near.Count == 0)
        {
            return 0;
        }

        var (inside, covered) = (0, 0);
        for (var i = 0; i < Grid; i++)
        {
            for (var j = 0; j < Grid; j++)
            {
                var (x, y) = (MinX + ((i + 0.5) / Grid * (MaxX - MinX)), MinY + ((j + 0.5) / Grid * (MaxY - MinY)));
                if (Contains(x, y))
                {
                    inside++;
                    covered += near.Any(o => o.Contains(x, y)) ? 1 : 0;
                }
            }
        }

        return inside == 0 ? 0 : (double)covered / inside;
    }

    private static double SignedArea((double X, double Y)[] ring)
    {
        var sum = 0d;
        for (int i = 0, j = ring.Length - 1; i < ring.Length; j = i++)
        {
            sum += (ring[j].X * ring[i].Y) - (ring[i].X * ring[j].Y);
        }

        return sum / 2;
    }
}
