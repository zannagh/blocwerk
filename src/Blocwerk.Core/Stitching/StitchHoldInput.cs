namespace Blocwerk.Core.Stitching;

/// <summary>
/// A hold as handed to the sidecar's matcher so it can be carried onto the stitched image.
/// Coordinates are normalised per axis against the OLD photo's dimensions.
/// </summary>
public sealed record StitchHoldInput(
    Guid Id,
    double X,
    double Y,
    double Radius,
    IReadOnlyList<StitchShapePoint>? ShapePoints,
    string? Color,
    int Category,
    int BoulderLinkCount);
