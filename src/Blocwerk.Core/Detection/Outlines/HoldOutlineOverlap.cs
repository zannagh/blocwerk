using Blocwerk.Core.Services;

namespace Blocwerk.Core.Detection.Outlines;

/// <summary>
/// Which holds of one photo lie substantially on top of each other: the part two outlines share is at least
/// <see cref="SubstantialShare"/> of the SMALLER hold's area. Holds that only touch or graze at an edge do not count.
/// The wall view outlines such holds, darker, next to the tapped one, so a hold sitting on another is not missed.
/// Pure: reads holds, writes nothing.
/// </summary>
public static class HoldOutlineOverlap
{
    /// <summary>Shared area over the smaller hold's area from which two holds count as overlapping.</summary>
    public const double SubstantialShare = 0.3;

    /// <summary>For every hold, the positions (in <paramref name="holds"/>) of the holds that overlap it substantially.</summary>
    /// <param name="holds">The holds of one photo, in the photo's normalized space.</param>
    /// <returns>One list per hold, in the order of <paramref name="holds"/>; empty for a hold nothing overlaps.</returns>
    public static IReadOnlyList<IReadOnlyList<int>> Find(IReadOnlyList<PanelHold> holds)
    {
        ArgumentNullException.ThrowIfNull(holds);
        var polys = holds.Select(Polygon).ToList();
        var boxes = polys.Select(p => (MinX: p.Min(v => v.X), MaxX: p.Max(v => v.X), MinY: p.Min(v => v.Y), MaxY: p.Max(v => v.Y))).ToList();
        var areas = polys.Select(p => ShapeGeometry.Area(p)).ToList();
        var found = holds.Select(_ => new List<int>()).ToList();
        for (int i = 0; i < polys.Count; i++)
        {
            for (int j = i + 1; j < polys.Count; j++)
            {
                double w = Math.Min(boxes[i].MaxX, boxes[j].MaxX) - Math.Max(boxes[i].MinX, boxes[j].MinX);
                double h = Math.Min(boxes[i].MaxY, boxes[j].MaxY) - Math.Max(boxes[i].MinY, boxes[j].MinY);
                double needed = SubstantialShare * Math.Min(areas[i], areas[j]);

                // The shared part cannot be larger than the box the two bounding boxes share, so most neighbours stop here.
                if (w <= 0 || h <= 0 || needed <= 0 || w * h < needed)
                {
                    continue;
                }

                if (ShapeOverlapArea.Measure(polys[i], polys[j]).Shared >= needed)
                {
                    found[i].Add(j);
                    found[j].Add(i);
                }
            }
        }

        return found;
    }

    private static P2[] Polygon(PanelHold hold) => hold.ShapePoints is { Count: >= 3 } shape
        ? shape.Select(s => new P2(hold.X + s.Dx, hold.Y + s.Dy)).ToArray()
        : ShapeObstacles.Circle(new P2(hold.X, hold.Y), hold.Radius);
}
