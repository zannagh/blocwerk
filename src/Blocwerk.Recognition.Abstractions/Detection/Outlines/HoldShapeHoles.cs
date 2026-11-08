using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Detection.Outlines;

/// <summary>Keeps a hold's interior holes (pockets) consistent with a changed outer outline.</summary>
public static class HoldShapeHoles
{
    /// <summary>
    /// The rings that still lie completely inside the new outline; the others are dropped (a pocket that the
    /// smoothed or clipped outline no longer encloses would be drawn outside the hold).
    /// </summary>
    /// <param name="holes">The rings, offsets in the same convention as <paramref name="shape"/>.</param>
    /// <param name="shape">The new outer outline.</param>
    /// <returns>The surviving rings, or null when none survive.</returns>
    public static List<List<ShapePoint>>? Inside(IEnumerable<IReadOnlyList<ShapePoint>>? holes, IReadOnlyList<ShapePoint> shape)
    {
        if (holes is null || shape.Count < 3)
        {
            return null;
        }

        var outline = shape.Select(p => new P2(p.Dx, p.Dy)).ToList();
        var kept = holes
            .Where(ring => ring.Count >= 3 && ring.All(p => ShapeGeometry.Contains(outline, new P2(p.Dx, p.Dy))))
            .Select(ring => ring.Select(p => new ShapePoint { Dx = p.Dx, Dy = p.Dy }).ToList())
            .ToList();
        return kept.Count == 0 ? null : kept;
    }
}
