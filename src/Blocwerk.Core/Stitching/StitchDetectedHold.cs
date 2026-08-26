namespace Blocwerk.Core.Stitching;

/// <summary>
/// A hold the detector found. Pure detection — no identity and no carryover verdict; matching an
/// existing hold to one of these is <see cref="StitchCarryover"/>'s job.
/// </summary>
/// <remarks>
/// <see cref="Id"/> is the detector's own id, not a Blocwerk hold id. Coordinates follow the
/// pipeline convention (see <see cref="StitchJobResult.CoordinateConvention"/>) and are normalised
/// against the FLAT master, except in <see cref="StitchJobResult.HoldsNatural"/> where they are
/// normalised against the natural one.
/// </remarks>
public sealed record StitchDetectedHold(
    string Id,
    double X,
    double Y,
    double Radius,
    double Confidence);
