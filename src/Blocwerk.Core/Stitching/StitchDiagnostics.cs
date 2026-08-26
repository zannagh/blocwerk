namespace Blocwerk.Core.Stitching;

/// <summary>Quality report for a finished stitch; persisted verbatim on the job row.</summary>
public sealed record StitchDiagnostics(
    IReadOnlyList<string>? ImagesUsed,
    IReadOnlyList<StitchRejectedImage>? ImagesRejected,
    string? ReferenceFrame,
    double Straightness,
    int InpaintedPx,
    double ElapsedSeconds,
    IReadOnlyList<string>? CoverageWarnings);
