namespace Blocwerk.Core.Stitching;

/// <summary>
/// One emitted variant of the natural master.
/// </summary>
/// <remarks>
/// The curvature numbers are nullable because they only mean anything under a curved projection: a
/// flat or panoramic variant emits a name and a size and nothing else. They must serialise as
/// ABSENT rather than as <c>0</c> — a zero <see cref="ThetaMaxDeg"/> would read as "no curve" and
/// silently produce the wrong flat-to-natural mapping.
/// </remarks>
public sealed record StitchCurveRef(
    string Name,
    string Artifact,
    string Display,
    int Width,
    int Height,
    double? ThetaMaxDeg = null,
    double? K = null,
    double? RadiusM = null);
