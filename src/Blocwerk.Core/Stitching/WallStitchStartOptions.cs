using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Stitching;

/// <summary>What the caller wants out of a stitch run, before it is translated to the wire format.</summary>
/// <param name="WallAngleDegrees">Wall inclination used to build the angled projection.</param>
/// <param name="DefaultProjection">Which projection should become the wall's default photo.</param>
/// <param name="TransferHolds">Carry the wall's existing holds onto the stitched image.</param>
public sealed record WallStitchStartOptions(
    double WallAngleDegrees,
    WallPhotoProjection DefaultProjection = WallPhotoProjection.Angled,
    bool TransferHolds = true);
