// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Geometry.View3D;

/// <summary>
/// What <see cref="HoldProtrusionEstimator"/> measures in: the photo-real scene's dense splat centres, or a capture's
/// COLMAP sparse points (no photo-real view: a few points per hold, so smaller minimums and a wider look around it;
/// coarser, and marked <see cref="HoldProtrusionSource.Sparse"/>).
/// </summary>
/// <param name="Source">The source the measurements are stored with.</param>
/// <param name="MinPoints">Fewest points inside the footprint for a measurement.</param>
/// <param name="MinRingPoints">Fewest points in the ring around it for the surface the hold is bolted to.</param>
/// <param name="InsideGrowMm">How far the footprint is grown for "inside", mm.</param>
/// <param name="RingOuterMm">Outer edge of that ring beyond the footprint, mm.</param>
/// <param name="BodyQuantile">Percentile of the inside heights taken as the hold's body height.</param>
public sealed record HoldProtrusionTuning(
    HoldProtrusionSource Source, int MinPoints, int MinRingPoints, double InsideGrowMm, double RingOuterMm, double BodyQuantile)
{
    /// <summary>Splat centres (the defaults fitted on The Attic).</summary>
    public static HoldProtrusionTuning Splat { get; } = new(HoldProtrusionSource.Splat, HoldProtrusionEstimator.MinPoints, 20, 10, 60, 0.8);

    /// <summary>Sparse points.</summary>
    public static HoldProtrusionTuning Sparse { get; } = new(HoldProtrusionSource.Sparse, 5, 8, 15, 90, 0.7);
}
