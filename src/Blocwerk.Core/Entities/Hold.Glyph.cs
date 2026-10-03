using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Entities;

/// <summary>
/// The stale-measurement rules for the glyph metric fields. Kept in ONE place so every edit path
/// applies the same rule:
/// <list type="bullet">
/// <item>carried into a new generation as the SAME physical hold → keep everything (a hold's size and
/// wall position do not change when the camera does) — <see cref="Clone"/> copies the fields as-is;</item>
/// <item>moved by a user → the plane position is stale (<see cref="InvalidateGlyphPosition"/>); the edit
/// path re-places it in the same save (<c>HoldGlyphRefresher</c>), so a moved hold never drops out of 3D;</item>
/// <item>reshaped or resized by a user → only the SIZE is stale (<see cref="InvalidateGlyphSize"/>): the
/// centre did not move, so the facet and plane position are kept. The detected pocket holes
/// (<see cref="ShapeHoles"/>) are dropped — they were measured against the detected outer outline and
/// cannot follow a hand-drawn one; re-detection restores them;</item>
/// <item>marked "changed" (a physically different hold) → sizes, footprint, protrusion and the appearance
/// fingerprint are stale (<see cref="InvalidateGlyphMeasurements"/>); it sits where the old one sat, so
/// the position is kept.</item>
/// </list>
/// The stored <see cref="FootprintMm"/> / <see cref="ProtrusionMm"/> survive a move or reshape: they carry
/// the outline key they were refined from, so the 3D view ignores them (and projects the edited outline)
/// until the next refinement rewrites them. That is the "stale" mark — nothing is deleted.
/// </summary>
public partial class Hold
{
    /// <summary>Tolerance for "the user moved/resized it" on normalized coordinates.</summary>
    public const double GeometryEditTolerance = 0.0001;

    /// <summary>
    /// Applies the rule for a user geometry edit. Call BEFORE writing the new geometry, or pass the
    /// flags you already computed. A move un-places the hold until the caller re-places it.
    /// </summary>
    /// <param name="moved">The centre moved.</param>
    /// <param name="reshaped">The drawn shape changed — see <see cref="IsReshape"/>.</param>
    public void InvalidateGlyphForEdit(bool moved, bool reshaped)
    {
        if (reshaped)
        {
            ShapeHoles = null;
            InvalidateGlyphSize();
            OutlineSource = HoldOutlineSource.Manual;
        }

        if (moved)
        {
            InvalidateGlyphPosition();
        }
    }

    /// <summary>
    /// Clears the facet and plane position (and how they were derived); sizes stay. The footprint and
    /// protrusion stay too: their outline key includes the centre, so they are already stale.
    /// </summary>
    public void InvalidateGlyphPosition()
    {
        FacetId = null;
        PlaneAMm = null;
        PlaneBMm = null;
        MetricSource = null;
    }

    /// <summary>
    /// Clears the metric sizes, including those inside the fingerprint. The facet and plane position
    /// stay: a reshape does not move the hold's centre.
    /// </summary>
    public void InvalidateGlyphSize()
    {
        WidthMm = null;
        HeightMm = null;
        AreaMm2 = null;
        var fingerprint = HoldFingerprint.FromJson(FingerprintJson);
        if (fingerprint is not null && (fingerprint.WidthMm ?? fingerprint.HeightMm ?? fingerprint.AreaMm2) is not null)
        {
            FingerprintJson = (fingerprint with { WidthMm = null, HeightMm = null, AreaMm2 = null }).ToJson();
        }
    }

    /// <summary>
    /// A physically different hold at the same spot: clears the sizes, footprint, protrusion AND the
    /// fingerprint. The plane position stays — it is still where the hold is.
    /// </summary>
    public void InvalidateGlyphMeasurements()
    {
        InvalidateGlyphSize();
        FingerprintJson = null;
        FootprintMm = null;
        ProtrusionMm = null;
    }

    /// <summary>
    /// Whether this hold is missing a placement or a size that its panel photo could give it again
    /// (see <c>HoldGlyphRefresher</c>). Virtual holds and holds without a panel have no photo to map from.
    /// </summary>
    /// <returns>Whether a refresh could restore something.</returns>
    public bool NeedsGlyphRefresh() =>
        WallPanelId is not null && !IsVirtual
        && (FacetId is null || PlaneAMm is null || PlaneBMm is null || WidthMm is null || HeightMm is null);

    /// <summary>
    /// Whether a geometry edit is a reshape (drops the pocket holes and the metric size). A hold drawn
    /// from its outline is only reshaped when that outline changes — its radius is then just the hit
    /// size, so a radius-only resize leaves the drawn shape, and its pocket, untouched. A plain circle is
    /// reshaped by a radius change. Evaluate BEFORE writing the new geometry.
    /// </summary>
    /// <param name="newRadius">The radius about to be written.</param>
    /// <param name="newShape">The outline about to be written; pass the current <see cref="ShapePoints"/> when it is kept.</param>
    /// <returns>Whether the edit reshapes the hold.</returns>
    public bool IsReshape(double newRadius, List<ShapePoint>? newShape)
    {
        if (ShapeDiffers(newShape))
        {
            return true;
        }

        var drawnFromOutline = newShape is { Count: > 0 };
        return !drawnFromOutline && Math.Abs(Radius - newRadius) > GeometryEditTolerance;
    }

    /// <summary>True when <paramref name="other"/> is a different outline than <see cref="ShapePoints"/>.</summary>
    /// <param name="other">The outline about to be written.</param>
    /// <returns>Whether the outline changes.</returns>
    public bool ShapeDiffers(List<ShapePoint>? other)
    {
        var mine = ShapePoints;
        if (mine is null || other is null)
        {
            return !ReferenceEquals(mine, other) && (mine?.Count ?? 0) + (other?.Count ?? 0) > 0;
        }

        if (mine.Count != other.Count)
        {
            return true;
        }

        for (var i = 0; i < mine.Count; i++)
        {
            if (Math.Abs(mine[i].Dx - other[i].Dx) > 1e-6 || Math.Abs(mine[i].Dy - other[i].Dy) > 1e-6)
            {
                return true;
            }
        }

        return false;
    }
}
