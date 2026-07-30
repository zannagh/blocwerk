using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry;

namespace Blocwerk.Core.Helpers;

/// <summary>
/// One wall segment after the layout solver has decided where it sits in the schematic: its
/// own orientation projection (<see cref="Plane"/>) plus the rigid-ish placement
/// (<see cref="Placement"/>) that pins it to its neighbour along their shared hinge.
/// </summary>
internal sealed class PlacedPanel
{
    public required IReadOnlyList<ShapePoint> Points { get; init; }

    public required PanelPlane Plane { get; init; }

    public SimilarityTransform Placement { get; set; } = SimilarityTransform.Identity;

    public bool Placed { get; set; }

    public SchematicPoint Project(double x, double y) => Placement.Apply(Plane.Apply(x, y));

    public SchematicPoint Project(ShapePoint p) => Project(p.Dx, p.Dy);
}
