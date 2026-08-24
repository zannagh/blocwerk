namespace Blocwerk.Core.Stitching;

/// <summary>
/// The <c>options</c> JSON part of a <c>POST /jobs</c> request.
/// <see cref="DefaultProjection"/> is the sidecar's wire spelling, <c>"angled"</c> or <c>"ortho"</c>.
/// <see cref="OldPhotoWidth"/>/<see cref="OldPhotoHeight"/> and <see cref="Holds"/> are only
/// meaningful when <see cref="TransferHolds"/> is true.
/// </summary>
public sealed record StitchJobOptions(
    double WallAngleDegrees,
    string DefaultProjection,
    bool TransferHolds,
    int? OldPhotoWidth,
    int? OldPhotoHeight,
    IReadOnlyList<StitchHoldInput> Holds);
