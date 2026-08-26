namespace Blocwerk.Core.Stitching;

/// <summary>
/// The <c>options</c> JSON part of a <c>POST /jobs</c> request.
/// </summary>
/// <remarks>
/// <see cref="Natural"/> picks the display projection and is deliberately an open string rather than
/// an enum: which projection the wall should get is an unsettled product question, and a new
/// candidate must not require a redeploy of both sides to try. Empty means "the sidecar's configured
/// default". <see cref="Curve"/> is <c>gentle</c>, <c>medium</c> or <c>strong</c>.
/// <see cref="OldPhotoWidth"/>/<see cref="OldPhotoHeight"/> and <see cref="Holds"/> are only
/// meaningful when <see cref="TransferHolds"/> is true.
/// </remarks>
public sealed record StitchJobOptions(
    string Natural,
    string Curve,
    double WallWidthM,
    double WallHeightM,
    bool TransferHolds,
    int? OldPhotoWidth,
    int? OldPhotoHeight,
    IReadOnlyList<StitchHoldInput> Holds);
