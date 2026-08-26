namespace Blocwerk.Core.Enums;

/// <summary>
/// Which of the wall's two stored images the viewer is showing. Maps one-to-one onto
/// <see cref="WallPhotoProjection"/>.
/// </summary>
/// <remarks>
/// The two are genuinely different renderings, so switching between them is not a pure image swap:
/// hold overlays live in flat space and have to be mapped for the natural view. See
/// <see cref="WallPhotoProjection"/>.
/// </remarks>
public enum WallViewMode
{
    /// <summary>The cylindrically remapped photo — how the wall reads in person. Default.</summary>
    Natural = 0,

    /// <summary>The undistorted flat base photo.</summary>
    Flat = 1,
}
