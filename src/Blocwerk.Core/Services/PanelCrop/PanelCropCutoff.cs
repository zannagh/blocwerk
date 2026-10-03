// <copyright file="PanelCropCutoff.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Services.PanelCrop;

/// <summary>
/// Which holds a crop cuts off: a hold whose centre leaves the new frame, or whose outline lies mostly (more than
/// <see cref="MaxOutsideShare"/> of its area) outside it. Evaluated on the hold AFTER mapping into the new frame, where
/// the frame is the unit square. A hold with no outline is judged by the circle its radius draws.
/// </summary>
public static class PanelCropCutoff
{
    /// <summary>The share of a hold's outline area that may fall outside the crop before the hold counts as cut off.</summary>
    public const double MaxOutsideShare = 0.5;

    /// <summary>Whether <paramref name="mapped"/> (already in the cropped frame) is cut off by it.</summary>
    /// <param name="mapped">The hold, mapped into the new frame.</param>
    /// <returns>True when the crop removes it.</returns>
    public static bool IsCutOff(Hold mapped)
    {
        if (mapped.X < 0 || mapped.X > 1 || mapped.Y < 0 || mapped.Y > 1)
        {
            return true;
        }

        var outline = Outline(mapped);
        var area = Math.Abs(Area(outline));
        if (area <= 1e-12)
        {
            return false;
        }

        var inside = Math.Abs(Area(ClipToUnitSquare(outline)));
        return inside / area < 1 - MaxOutsideShare;
    }

    /// <summary>The hold's outline as absolute points: its traced polygon, else a 16-gon of its radius.</summary>
    private static List<(double X, double Y)> Outline(Hold hold)
    {
        if (hold.ShapePoints is { Count: >= 3 } shape)
        {
            return shape.Select(p => (hold.X + p.Dx, hold.Y + p.Dy)).ToList();
        }

        const int sides = 16;
        return Enumerable.Range(0, sides)
            .Select(i => i * 2 * Math.PI / sides)
            .Select(a => (hold.X + (Math.Cos(a) * hold.Radius), hold.Y + (Math.Sin(a) * hold.Radius)))
            .ToList();
    }

    /// <summary>Signed shoelace area.</summary>
    private static double Area(List<(double X, double Y)> polygon)
    {
        var sum = 0.0;
        for (var i = 0; i < polygon.Count; i++)
        {
            var (a, b) = (polygon[i], polygon[(i + 1) % polygon.Count]);
            sum += (a.X * b.Y) - (b.X * a.Y);
        }

        return sum / 2;
    }

    /// <summary>Sutherland–Hodgman against the four edges of the unit square.</summary>
    private static List<(double X, double Y)> ClipToUnitSquare(List<(double X, double Y)> polygon)
    {
        var result = polygon;
        result = Clip(result, p => p.X >= 0, (a, b) => Cross(a, b, (0 - a.X) / (b.X - a.X)));
        result = Clip(result, p => p.X <= 1, (a, b) => Cross(a, b, (1 - a.X) / (b.X - a.X)));
        result = Clip(result, p => p.Y >= 0, (a, b) => Cross(a, b, (0 - a.Y) / (b.Y - a.Y)));
        result = Clip(result, p => p.Y <= 1, (a, b) => Cross(a, b, (1 - a.Y) / (b.Y - a.Y)));
        return result;
    }

    private static List<(double X, double Y)> Clip(
        List<(double X, double Y)> polygon,
        Func<(double X, double Y), bool> inside,
        Func<(double X, double Y), (double X, double Y), (double X, double Y)> intersect)
    {
        var output = new List<(double X, double Y)>(polygon.Count + 4);
        for (var i = 0; i < polygon.Count; i++)
        {
            var current = polygon[i];
            var previous = polygon[(i + polygon.Count - 1) % polygon.Count];
            if (inside(current))
            {
                if (!inside(previous))
                {
                    output.Add(intersect(previous, current));
                }

                output.Add(current);
            }
            else if (inside(previous))
            {
                output.Add(intersect(previous, current));
            }
        }

        return output;
    }

    private static (double X, double Y) Cross((double X, double Y) a, (double X, double Y) b, double t) =>
        (a.X + ((b.X - a.X) * t), a.Y + ((b.Y - a.Y) * t));
}
