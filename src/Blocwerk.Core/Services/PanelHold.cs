using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Services;

/// <summary>
/// A single hold belonging to a big-wall panel, in the panel image's normalized coordinate
/// space, for drawing the overlap confirmation overlay. Positions and radius are 0..1 fractions
/// of the panel image.
/// </summary>
/// <param name="Id">The hold's id.</param>
/// <param name="X">Normalized centre X (0..1).</param>
/// <param name="Y">Normalized centre Y (0..1).</param>
/// <param name="Radius">Normalized radius (0..1).</param>
/// <param name="Color">The detected colour name, or null.</param>
/// <param name="Category">
/// Whether the hold is a hand or a foot hold. Mirrors <see cref="Hold.Category"/>; the overlays use it
/// to draw a foot as a smaller rounded square, the way the wall editor and the hold picker already do.
/// </param>
/// <param name="ShapePoints">The hold's custom shape outline (offsets from the centre), or null for a circle.</param>
/// <param name="ShapeHoles">The outline's interior holes (pocket/donut), same convention as
/// <paramref name="ShapePoints"/>; null for a solid hold. Visual only — the hold is hit on its full outline.</param>
/// <param name="IsVirtual">
/// True when the hold is a user-placed placeholder that isn't visible in the wall photo (e.g. a
/// virtual TOP). Drawn with a dashed outline so it doesn't read as a real hold on the image.
/// </param>
public record PanelHold(
    Guid Id,
    double X,
    double Y,
    double Radius,
    string? Color,
    HoldCategory Category,
    List<ShapePoint>? ShapePoints = null,
    bool IsVirtual = false,
    List<List<ShapePoint>>? ShapeHoles = null);
