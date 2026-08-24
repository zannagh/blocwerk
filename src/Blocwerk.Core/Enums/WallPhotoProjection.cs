namespace Blocwerk.Core.Enums;

/// <summary>
/// Which rectified view of the wall plane a stored wall photo represents.
/// </summary>
/// <remarks>
/// Both projections come out of the same stitch geometry and differ only by a vertical scale of
/// <c>cos(wallAngle)</c>. Because hold coordinates are normalised per axis
/// (<c>X = px/imageWidth</c>, <c>Y = px/imageHeight</c>), a pure vertical scale cancels out —
/// <c>(y·c)/(H·c) == y/H</c> — so ONE hold set serves both projections and switching projection
/// is a pure image swap. Never convert hold coordinates between projections.
/// <para>
/// Named <c>WallPhotoProjection</c> rather than <c>WallProjection</c> on purpose: the static
/// helper <see cref="Blocwerk.Core.Helpers.WallProjection"/> already owns that name and is
/// imported alongside <c>Blocwerk.Core.Enums</c> in several Razor components.
/// </para>
/// </remarks>
public enum WallPhotoProjection
{
    /// <summary>Head-on view that keeps the wall's physical steepness (vertical axis scaled by cos(angle)).</summary>
    Angled = 0,

    /// <summary>Fully fronto-parallel view of the wall plane, with no foreshortening.</summary>
    Ortho = 1,
}
