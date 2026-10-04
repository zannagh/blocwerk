using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Detection.Outlines;

/// <summary>A hold's footprint on its photo as a polygon in normalized coordinates (its outline, else its circle).</summary>
internal static class HoldShapePolygon
{
    /// <summary>The footprint polygon of <paramref name="hold"/>.</summary>
    /// <param name="hold">The hold.</param>
    /// <returns>The polygon vertices.</returns>
    public static P2[] Of(Hold hold) => hold.ShapePoints is { Count: >= 3 } shape
        ? shape.Select(s => new P2(hold.X + s.Dx, hold.Y + s.Dy)).ToArray()
        : ShapeObstacles.Circle(new P2(hold.X, hold.Y), hold.Radius);
}
