using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Detection.Outlines;

/// <summary>
/// Conversions between an outline polygon and the <see cref="Hold.ShapePoints"/> storage convention.
/// </summary>
/// <remarks>
/// <para><b>The convention</b> (as rendered by <c>HoldShape.razor</c>): a shape point is an offset from the
/// hold centre in the SAME per-axis normalized space as <see cref="Hold.X"/>/<see cref="Hold.Y"/> —
/// <see cref="ShapePoint.Dx"/> is a fraction of the image WIDTH and <see cref="ShapePoint.Dy"/> a fraction of
/// the image HEIGHT. The renderer draws vertex <c>i</c> at <c>((X + Dx)·100, (Y + Dy)·100)</c> inside an SVG
/// with <c>viewBox="0 0 100 100" preserveAspectRatio="none"</c>, i.e. the absolute vertex is simply
/// <c>(X + Dx, Y + Dy)</c> in normalized image space. It is NOT scaled by <see cref="Hold.Radius"/> and
/// there is no aspect correction, so on a 4:3 photo a unit Dx spans 4/3 the pixels of a unit Dy.</para>
/// <para>The renderer draws the polygon whenever there are ≥ 3 points, otherwise it falls back to the
/// circle (or the foot square). The server-side carry code (<c>WallBigUpdateService.Carry</c>) writes
/// <c>Dx = vertex.X - hold.X</c> the same way.</para>
/// <para>Note the circle is drawn as <c>r = Radius·100</c> in that non-uniform viewBox, so it is an ellipse
/// on non-square photos, while the detector's radius is normalized by the LONGER side: on a landscape
/// photo the rendered circle is correct horizontally and ~25 % too short vertically.</para>
/// </remarks>
public static class HoldOutlineGeometry
{
    /// <summary>Converts an absolute normalized polygon into shape points around an anchor.</summary>
    /// <param name="polygon">Vertices in normalized image coordinates.</param>
    /// <param name="anchorX">The hold's normalized centre X.</param>
    /// <param name="anchorY">The hold's normalized centre Y.</param>
    /// <returns>Shape points, rounded to 5 decimals like <see cref="ShapePoint.DefaultOctagon"/>.</returns>
    public static List<ShapePoint> ToShapePoints(IEnumerable<NormalizedPoint> polygon, double anchorX, double anchorY)
    {
        return polygon
            .Select(p => new ShapePoint
            {
                Dx = Math.Round(p.X - anchorX, 5),
                Dy = Math.Round(p.Y - anchorY, 5),
            })
            .ToList();
    }

    /// <summary>Converts stored shape points back into the absolute normalized polygon the renderer draws.</summary>
    /// <param name="shapePoints">The hold's shape points.</param>
    /// <param name="holdX">The hold's normalized centre X.</param>
    /// <param name="holdY">The hold's normalized centre Y.</param>
    /// <returns>The absolute vertices.</returns>
    public static List<NormalizedPoint> ToPolygon(IEnumerable<ShapePoint> shapePoints, double holdX, double holdY)
    {
        return shapePoints.Select(sp => new NormalizedPoint(holdX + sp.Dx, holdY + sp.Dy)).ToList();
    }
}
