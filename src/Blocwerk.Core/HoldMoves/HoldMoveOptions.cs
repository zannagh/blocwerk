// <copyright file="HoldMoveOptions.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.HoldMoves;

/// <summary>
/// Thresholds for judging how far a hold moved between two panel generations. Bound from the <c>HoldMoves</c>
/// configuration section; the defaults are the owner's rule (10 cm).
/// </summary>
public sealed class HoldMoveOptions
{
    /// <summary>The configuration section name.</summary>
    public const string Section = "HoldMoves";

    /// <summary>A hold that moved at most this far (mm) stays on its boulders (marked for review); farther, it comes off them.</summary>
    public double CutoffMm { get; set; } = 100;

    /// <summary>Below this (mm) a 3D measurement is registration noise, not a move.</summary>
    public double NoiseMm { get; set; } = 30;

    /// <summary>Below this (mm) a photo-warp estimate is alignment noise, not a move.</summary>
    public double NoiseMm2D { get; set; } = 60;

    /// <summary>An elongated hold turned by at least this many degrees counts as moved even when it did not shift.</summary>
    public double MinRotationDeg { get; set; } = 45;

    /// <summary>Two holds this similar (fingerprint, 0..1) may be the same kind of hold on the wall.</summary>
    public double SimilarityMin { get; set; } = 0.85;

    /// <summary>Two holds whose sizes differ by more than this fraction are not look-alikes.</summary>
    public double SizeTolerance { get; set; } = 0.25;

    /// <summary>Two holds whose depths differ by more than this (mm) are not look-alikes.</summary>
    public double DepthToleranceMm { get; set; } = 20;
}
