namespace Blocwerk.Core.Stitching;

/// <summary>
/// The <c>result</c> object of a succeeded job. <see cref="Ortho"/> and <see cref="Angled"/> are the
/// full-resolution masters; the two display artifacts are the display-resolution copies that go
/// into the database. <see cref="VerticalScale"/> equals <c>cos(WallAngleDegrees)</c>.
/// </summary>
public sealed record StitchJobResult(
    StitchArtifactRef Ortho,
    StitchArtifactRef Angled,
    string DisplayOrtho,
    string DisplayAngled,
    double WallAngleDegrees,
    double VerticalScale,
    StitchDiagnostics? Diagnostics,
    IReadOnlyList<StitchResultHold>? Holds);
