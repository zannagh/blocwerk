// <copyright file="HoldOutlineSmoothing.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// Display-only rounding of a hold outline: Chaikin corner cutting over the closed ring, so a traced
/// outline's sharp corners read like the rounded edge of a real hold. It returns new points and never
/// touches the stored outline.
/// </summary>
public static class HoldOutlineSmoothing
{
    /// <summary>Two passes: corners are rounded off without the shape shrinking noticeably.</summary>
    public const int Passes = 2;

    /// <summary>Returns the ring rounded by <paramref name="passes"/> Chaikin passes, or the ring itself when it is not a polygon.</summary>
    /// <param name="ring">The closed outline, as offsets from the hold's centre.</param>
    /// <param name="passes">How many corner-cutting passes to run.</param>
    /// <returns>The rounded ring (a new list), or <paramref name="ring"/> unchanged.</returns>
    public static IReadOnlyList<ShapePoint>? Smooth(IReadOnlyList<ShapePoint>? ring, int passes = Passes)
    {
        if (ring is not { Count: >= 3 } || passes <= 0)
        {
            return ring;
        }

        var current = ring;
        for (var pass = 0; pass < passes; pass++)
        {
            current = CutCorners(current);
        }

        return current;
    }

    /// <summary>Rounds every ring of a pocket hold's holes, see <see cref="Smooth"/>.</summary>
    /// <param name="holes">The holes, or null.</param>
    /// <returns>The rounded holes, or null.</returns>
    public static IReadOnlyList<IReadOnlyList<ShapePoint>>? SmoothRings(IReadOnlyList<IReadOnlyList<ShapePoint>>? holes) =>
        holes?.Select(hole => Smooth(hole) ?? hole).ToList();

    // One Chaikin pass over a closed ring: each edge is replaced by the points a quarter and three
    // quarters along it, which cuts every corner and doubles the point count.
    private static List<ShapePoint> CutCorners(IReadOnlyList<ShapePoint> ring)
    {
        var result = new List<ShapePoint>(ring.Count * 2);
        for (var i = 0; i < ring.Count; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % ring.Count];
            result.Add(new ShapePoint { Dx = (0.75 * a.Dx) + (0.25 * b.Dx), Dy = (0.75 * a.Dy) + (0.25 * b.Dy) });
            result.Add(new ShapePoint { Dx = (0.25 * a.Dx) + (0.75 * b.Dx), Dy = (0.25 * a.Dy) + (0.75 * b.Dy) });
        }

        return result;
    }
}
