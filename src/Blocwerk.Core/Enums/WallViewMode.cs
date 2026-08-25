namespace Blocwerk.Core.Enums;

/// <summary>
/// Which view of the wall the viewer is showing. The first two map directly onto stored images
/// (<see cref="WallPhotoProjection"/>); the third is a client-side morph of the ortho image.
/// </summary>
/// <remarks>
/// <para><see cref="Natural"/> is the default angled photo (the wall's real steepness).</para>
/// <para><see cref="Ortho"/> is the stored fronto-parallel alternate — a pure image swap.</para>
/// <para>
/// <see cref="Cylindric"/> derives from the ortho image at render time: a cylinder morph that
/// curves a very wide wall back toward the viewer. Both the image and the SVG hold overlay are
/// warped through the same map so they stay registered — see
/// <see cref="Blocwerk.Core.Helpers.CylindricMap"/>. Nothing is stored for it.
/// </para>
/// </remarks>
public enum WallViewMode
{
    /// <summary>The angled photo, keeping the wall's physical steepness. Default.</summary>
    Natural = 0,

    /// <summary>The stored fronto-parallel (ortho) alternate photo.</summary>
    Ortho = 1,

    /// <summary>A client-side cylinder morph of the ortho image, for very wide walls.</summary>
    Cylindric = 2,
}
