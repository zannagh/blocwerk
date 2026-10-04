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
    public double NoiseMm2D { get; set; } = 25;

    /// <summary>
    /// When the 3D measure and the photo estimate both exist they must agree within this (mm, or half the distance when that is
    /// more) for the 3D one to be used. A stored placement can be stale or a registration off for one hold; the photo estimate
    /// does not depend on either, and when it says the hold stayed, it did.
    /// </summary>
    public double AgreeMm { get; set; } = 40;

    /// <summary>An elongated hold turned by at least this many degrees counts as moved even when it did not shift.</summary>
    public double MinRotationDeg { get; set; } = 45;

    /// <summary>Carried holds within this distance (mm) on the same facet form a hold's unmoved neighbourhood.</summary>
    public double NeighbourRadiusMm { get; set; } = 500;

    /// <summary>A hold is only measured differentially against at least this many neighbours; with fewer there is no 3D verdict.</summary>
    public int MinNeighbours { get; set; } = 5;

    /// <summary>A neighbourhood whose own residual spread (mm) is above this is too noisy to trust a verdict from.</summary>
    public double MaxSpreadMm { get; set; } = 25;

    /// <summary>The 3D floor of a hold is at least this many times its neighbourhood's residual spread.</summary>
    public double SpreadFactor { get; set; } = 3.5;

    /// <summary>
    /// When true, a confident move past the removal margin takes the hold off its boulders even without an accepted
    /// "this hold moved", as long as the photo estimate backs it (it is the photo estimate, or the photo estimate agrees with
    /// the 3D measure). A 3D measure alone never does. Default false: only a person's accepted relocation removes a hold, and
    /// every other move past the cutoff is "possibly moved", kept for review.
    /// </summary>
    public bool RemoveUnconfirmedBeyondCutoff { get; set; }

    /// <summary>A hold is only taken off its boulders at this multiple of the cutoff (and only when confirmed and confident).</summary>
    public double RemovalMargin { get; set; } = 1.5;

    /// <summary>Two holds this similar (fingerprint, 0..1) may be the same kind of hold on the wall.</summary>
    public double SimilarityMin { get; set; } = 0.85;

    /// <summary>Two holds whose sizes differ by more than this fraction are not look-alikes.</summary>
    public double SizeTolerance { get; set; } = 0.25;

    /// <summary>Two holds whose depths differ by more than this (mm) are not look-alikes.</summary>
    public double DepthToleranceMm { get; set; } = 20;
}
