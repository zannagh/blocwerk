namespace Blocwerk.Core.Stitching;

/// <summary>
/// The display views the pipeline rendered and which projection made them.
/// <see cref="Default"/> names the entry in <see cref="Curves"/> that the natural master was
/// rendered at; its <see cref="StitchCurveRef.ThetaMaxDeg"/> and <see cref="StitchCurveRef.K"/> are
/// what a renderer needs to map a flat-space hold onto the natural image.
/// </summary>
public sealed record StitchCurvature(
    string Projection,
    string Default,
    double RequestedThetaMaxDeg,
    double ViewDistM,
    double EyeFrac,
    IReadOnlyList<StitchCurveRef>? Curves);
