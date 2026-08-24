namespace Blocwerk.Core.Stitching;

/// <summary>
/// A hold as placed on the stitched image. Coordinates are normalised per axis and are therefore
/// valid for BOTH the ortho and the angled artifact — see <see cref="Enums.WallPhotoProjection"/>.
/// <see cref="Classification"/> is <c>matched</c>, <c>uncertain</c> or <c>missing</c>.
/// </summary>
public sealed record StitchResultHold(
    Guid Id,
    double X,
    double Y,
    double Radius,
    IReadOnlyList<StitchShapePoint>? ShapePoints,
    string Classification,
    double Confidence);
