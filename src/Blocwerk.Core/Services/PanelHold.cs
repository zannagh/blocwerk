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
/// <param name="ShapePoints">The hold's custom shape outline (offsets from the centre), or null for a circle.</param>
/// <param name="Name">The hold's name, or null. Shown in the read-only tap bubble.</param>
/// <param name="Material">The hold's material, or null. Shown in the read-only tap bubble.</param>
/// <param name="Category">Hand vs foot — gates whether the hand sub-type is meaningful.</param>
/// <param name="HandType">The hand sub-type (Jug/Crimp/…), or null. Shown in the read-only tap bubble.</param>
public record PanelHold(
    Guid Id,
    double X,
    double Y,
    double Radius,
    string? Color,
    List<ShapePoint>? ShapePoints = null,
    string? Name = null,
    HoldMaterial? Material = null,
    HoldCategory Category = HoldCategory.Hand,
    HoldHandType? HandType = null);
