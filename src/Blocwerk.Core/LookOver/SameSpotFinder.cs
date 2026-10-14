// <copyright file="SameSpotFinder.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.HoldStacks;

namespace Blocwerk.Core.LookOver;

/// <summary>
/// Which new hold sits on the spot of an old hold that was kept in place. Looser on purpose than the duplicate-holds
/// review (<see cref="Detection.Outlines.HoldDuplicateFinder"/> wants a similar colour and outlines that mostly
/// overlap): after a re-set the hold on the spot is another one, so only the position is compared. Two answers of
/// different strength: a new hold is ON THE SPOT when the two centres are within the larger radius of each other or
/// the old centre is inside the new outline; the old hold lies UNDER the new one (<see cref="Covers"/>) only when its
/// centre is inside the new hold's own outline or circle. The second is what a bulk removal may rely on.
/// A hold and what it is stacked on (<see cref="Hold.StackedOnHoldId"/>, at any depth) are never each other's "hold on
/// the spot": the hold that sat on a volume before the update is not under that volume, and the volume did not take
/// its place. Only the stored relation counts here, never the outlines: an old hold that was NOT found again, with a
/// new larger hold over its spot, is exactly what "lies under the new one" is for.
/// </summary>
internal static class SameSpotFinder
{
    /// <summary>The new hold on the spot of <paramref name="kept"/>: one that covers it if there is one, else the nearest; or null.</summary>
    /// <param name="kept">The old hold's copy on the new photo.</param>
    /// <param name="fresh">The new holds of the same photo.</param>
    /// <param name="size">The photo's pixel size (positions are normalised per axis, radii to the long side); null treats it as square.</param>
    /// <returns>The hold on the spot.</returns>
    public static Hold? Nearest(Hold kept, IEnumerable<Hold> fresh, (int Width, int Height)? size)
    {
        var (width, height) = Size(size);
        var longSide = Math.Max(width, height);
        var others = fresh as IReadOnlyList<Hold> ?? [.. fresh];
        var on = others.Append(kept).GroupBy(h => h.Id).ToDictionary(g => g.Key, g => g.First().StackedOnHoldId);
        return others
            .Where(other => !HoldStackOrder.Below(on, kept.Id).Contains(other.Id) && !HoldStackOrder.Below(on, other.Id).Contains(kept.Id))
            .Select(other => (Other: other, Distance: Distance(kept, other, width, height), Covers: Covers(other, kept, size)))
            .Where(c => c.Covers || c.Distance <= Math.Max(kept.Radius, c.Other.Radius) * longSide)
            .OrderByDescending(c => c.Covers)
            .ThenBy(c => c.Distance)
            .Select(c => c.Other)
            .FirstOrDefault();
    }

    /// <summary>The centre of <paramref name="old"/> lies inside <paramref name="newHold"/>: its traced outline when it has one, else its circle.</summary>
    /// <param name="newHold">The new hold.</param>
    /// <param name="old">The old hold kept in place.</param>
    /// <param name="size">The photo's pixel size; null treats it as square.</param>
    /// <returns>True when the old hold lies under the new one.</returns>
    public static bool Covers(Hold newHold, Hold old, (int Width, int Height)? size)
    {
        if (newHold.StackedOnHoldId == old.Id || old.StackedOnHoldId == newHold.Id)
        {
            return false;
        }

        if (newHold.ShapePoints is { Count: >= 3 } outline)
        {
            // Containment survives the per-axis scaling, so the normalised coordinates are tested as they are.
            return Inside(old.X, old.Y, outline.Select(p => (newHold.X + p.Dx, newHold.Y + p.Dy)).ToList());
        }

        var (width, height) = Size(size);
        return Distance(newHold, old, width, height) <= newHold.Radius * Math.Max(width, height);
    }

    private static (double Width, double Height) Size((int Width, int Height)? size) =>
        size is { Width: > 0, Height: > 0 } s ? (s.Width, s.Height) : (1d, 1d);

    private static double Distance(Hold a, Hold b, double width, double height)
    {
        var dx = (a.X - b.X) * width;
        var dy = (a.Y - b.Y) * height;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    /// <summary>Even-odd ray casting.</summary>
    private static bool Inside(double x, double y, IReadOnlyList<(double X, double Y)> polygon)
    {
        var inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            var (a, b) = (polygon[i], polygon[j]);
            if ((a.Y > y) != (b.Y > y) && x < ((b.X - a.X) * (y - a.Y) / (b.Y - a.Y)) + a.X)
            {
                inside = !inside;
            }
        }

        return inside;
    }
}
