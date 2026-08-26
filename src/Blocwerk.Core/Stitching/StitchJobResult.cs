namespace Blocwerk.Core.Stitching;

/// <summary>
/// What the sidecar hands back for a finished job.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="FlatMaster"/> is the metric, fronto-parallel composite: the surface every hold
/// coordinate in this result is normalised against, and the one to re-run detection or carryover on.
/// <see cref="NaturalMaster"/> is the curved photographic view for display, at the curvature named
/// by <c>Curvature.Default</c>. The two are related by a cylindrical remap, NOT by a scale, so they
/// do not share a pixel grid — see <see cref="Enums.WallPhotoProjection"/>.
/// </para>
/// <para>
/// <see cref="CamerasJson"/> is an artifact NAME to download, not the JSON itself.
/// <see cref="Holds"/> and <see cref="HoldsNatural"/> are raw detections with no identity;
/// everything about which existing hold survived lives in <see cref="Carryover"/>.
/// </para>
/// </remarks>
public sealed record StitchJobResult(
    StitchArtifactRef FlatMaster,
    StitchArtifactRef NaturalMaster,
    string DisplayFlat,
    string DisplayNatural,
    string CamerasJson,
    string? CoordinateConvention,
    double WallWidthM,
    double WallHeightM,
    StitchCurvature? Curvature,
    IReadOnlyList<StitchDetectedHold>? Holds,
    IReadOnlyList<StitchDetectedHold>? HoldsNatural,
    StitchCarryover? Carryover,
    StitchDiagnostics? Diagnostics);
