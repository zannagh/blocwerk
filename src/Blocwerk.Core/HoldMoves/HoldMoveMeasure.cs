// <copyright file="HoldMoveMeasure.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;

namespace Blocwerk.Core.HoldMoves;

/// <summary>How far one hold moved between two generations.</summary>
/// <param name="DistanceMm">The displacement on the wall, mm.</param>
/// <param name="Source">Whether it was measured in 3D or estimated from the photo warp.</param>
/// <param name="RotationDeg">How far an elongated hold turned (0..90), or null when it cannot be told.</param>
/// <param name="SpreadMm">The typical residual of the hold's unmoved neighbours (3D differential), the measure's noise.</param>
/// <param name="Confident">Whether the neighbourhood was large and quiet enough to trust the distance.</param>
/// <param name="Corroborated">The distance comes from the photo estimate, or a 3D measure the photo estimate agrees with.</param>
/// <param name="ThreeDMm">The 3D differential distance when one was computed, even when the photo estimate was used instead (diagnostics).</param>
/// <param name="RawMm">The displacement before the neighbourhood's was subtracted (diagnostics: the registration error included).</param>
public sealed record HoldMoveMeasure(
    double DistanceMm, HoldMoveSource Source, double? RotationDeg, double? SpreadMm = null, bool Confident = true, double? RawMm = null, double? ThreeDMm = null, bool Corroborated = false);

/// <summary>One carried hold pair and what its move does to the boulders.</summary>
/// <param name="OldHoldId">The old hold.</param>
/// <param name="NewHoldId">Its successor.</param>
/// <param name="Measure">The measurement.</param>
/// <param name="Outcome">The rule's verdict.</param>
public sealed record PlannedMove(Guid OldHoldId, Guid NewHoldId, HoldMoveMeasure Measure, HoldMoveOutcome Outcome);
