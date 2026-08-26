namespace Blocwerk.Core.Enums;

/// <summary>
/// Which rendering of the wall a stored wall photo represents.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Flat"/> and <see cref="Natural"/> are DIFFERENT geometries, not two scalings of one
/// image. The pipeline builds the flat base first and then produces the natural view by a
/// cylindrical remap of it — a per-column, non-affine warp. Nothing about it cancels out under
/// per-axis coordinate normalisation, so the two images do not share a pixel grid.
/// </para>
/// <para>
/// Hold coordinates are canonical in FLAT space: every stored <see cref="Entities.Hold"/> X/Y/Radius
/// is measured against the flat master. Rendering those holds on the natural view means mapping
/// them through the stored camera/curve parameters (<see cref="Entities.Wall.CamerasJson"/> plus
/// <see cref="Entities.Wall.PhotoCurvature"/>). Never assume a projection change is a pure image
/// swap, and never write natural-space coordinates back onto a hold.
/// </para>
/// <para>
/// Named <c>WallPhotoProjection</c> rather than <c>WallProjection</c> on purpose: the static
/// helper <see cref="Blocwerk.Core.Helpers.WallProjection"/> already owns that name and is
/// imported alongside <c>Blocwerk.Core.Enums</c> in several Razor components.
/// </para>
/// </remarks>
public enum WallPhotoProjection
{
    /// <summary>
    /// The cylindrically remapped view: how the wall reads to someone standing in front of it.
    /// Derived from <see cref="Flat"/>, never the other way round.
    /// </summary>
    Natural = 0,

    /// <summary>
    /// The undistorted flat base the pipeline stitches first. The canonical space for hold
    /// coordinates and the surface recognition runs against.
    /// </summary>
    Flat = 1,
}
