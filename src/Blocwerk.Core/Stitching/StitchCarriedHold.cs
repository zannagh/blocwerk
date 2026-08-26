namespace Blocwerk.Core.Stitching;

/// <summary>
/// One of the wall's existing holds, placed on the new flat master. Used for both the
/// <c>carried</c> and the <c>missing</c> list.
/// </summary>
/// <remarks>
/// <see cref="Classification"/> is <c>CARRIED_OVER</c> when a detection claimed the hold and
/// <c>MISSING</c> when none did. A MISSING hold still carries its transferred position, so the app
/// can show where it used to be. <see cref="MatchDistancePx"/> is in FLAT-master pixels, not radii:
/// normalising it needs the hold's radius and the master's pixel size.
/// </remarks>
public sealed record StitchCarriedHold(
    Guid Id,
    double X,
    double Y,
    double Radius,
    IReadOnlyList<StitchShapePoint>? ShapePoints,
    string Classification,
    string? MatchedDetectionId,
    double? MatchDistancePx,
    bool? ColourAgrees,
    int BoulderLinkCount,
    bool InFrame,
    string? Reason);
