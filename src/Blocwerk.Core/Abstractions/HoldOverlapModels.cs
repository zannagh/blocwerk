namespace Blocwerk.Core.Abstractions;

/// <summary>
/// A hold detected on one photo, expressed as the matcher's input. Centres are
/// normalized (0..1) so they are resolution-independent; <see cref="SizeNorm"/> and
/// <see cref="Color"/> are optional and only sharpen the appearance/colour tie-breaks.
/// </summary>
/// <param name="Id">Detector-assigned id, unique within the photo.</param>
/// <param name="X">Normalized centre X (0..1).</param>
/// <param name="Y">Normalized centre Y (0..1).</param>
/// <param name="SizeNorm">Optional normalized max(width,height) / max(imgW,imgH) of the hold box.</param>
/// <param name="Color">Optional colour sampled from the hold blob (CIE Lab).</param>
public sealed record MatcherHold(
    int Id,
    double X,
    double Y,
    double? SizeNorm = null,
    MatcherColor? Color = null);

/// <summary>
/// A colour in CIE Lab space (OpenCV 8-bit convention: L,a,b each 0..255) sampled from
/// the segmented hold blob. Used only as a gated tie-break and last-resort rescue.
/// </summary>
/// <param name="L">Lightness channel.</param>
/// <param name="A">Green–red channel.</param>
/// <param name="B">Blue–yellow channel.</param>
public sealed record MatcherColor(double L, double A, double B);

/// <summary>
/// A single proposed correspondence: the same physical hold seen in both photos.
/// </summary>
/// <param name="LeftHoldId">Id of the hold in the left photo.</param>
/// <param name="RightHoldId">Id of the hold in the right photo.</param>
/// <param name="Confidence">0..1 confidence. Ship threshold ≈ 0.45 auto-accept; 0.30–0.45 = confirm.</param>
/// <param name="Moved">True when the geometric residual is large — flag for manual re-check.</param>
/// <param name="ResidualPx">Warp-field prediction error in right-image pixels.</param>
/// <param name="Rescue">Set only for low-confidence rescues (e.g. "colour"); null for normal matches.</param>
public sealed record HoldOverlapProposal(
    int LeftHoldId,
    int RightHoldId,
    double Confidence,
    bool Moved,
    double ResidualPx,
    string? Rescue);

/// <summary>
/// The matcher output: one-to-one proposals plus the holds left unmatched inside the
/// overlap band (the natural "new hold / removed hold" bucket).
/// </summary>
/// <param name="Proposals">Proposed correspondences, sorted by descending confidence.</param>
/// <param name="UnmatchedLeft">Left-hold ids in band with no proposed twin.</param>
/// <param name="UnmatchedRight">Right-hold ids in band with no proposed twin.</param>
public sealed record HoldOverlapResult(
    IReadOnlyList<HoldOverlapProposal> Proposals,
    IReadOnlyList<int> UnmatchedLeft,
    IReadOnlyList<int> UnmatchedRight);
