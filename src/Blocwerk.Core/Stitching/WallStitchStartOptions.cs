using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Stitching;

/// <summary>What the caller wants out of a stitch run, before it is translated to the wire format.</summary>
/// <param name="WallWidthM">Physical wall width in metres; the pipeline is metric, not angle-based.</param>
/// <param name="WallHeightM">Physical wall height in metres.</param>
/// <param name="DefaultProjection">
/// Which of the two stored images becomes the wall's default photo. App-side only — the sidecar is
/// told which natural projection to RENDER via <paramref name="Natural"/>, which is a different
/// question.
/// </param>
/// <param name="Natural">
/// Name of the natural projection to render, e.g. <c>flat</c> or <c>cylindrical</c>. Empty means
/// the sidecar's configured default; deliberately an open string, see <see cref="StitchJobOptions"/>.
/// </param>
/// <param name="Curve">Curve strength: <c>gentle</c>, <c>medium</c> or <c>strong</c>.</param>
/// <param name="TransferHolds">Carry the wall's existing holds onto the stitched image.</param>
public sealed record WallStitchStartOptions(
    double WallWidthM,
    double WallHeightM,
    WallPhotoProjection DefaultProjection = WallPhotoProjection.Natural,
    string Natural = "",
    string Curve = "gentle",
    bool TransferHolds = true);
