// <copyright file="HoldMoveMeasure.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;

namespace Blocwerk.Core.HoldMoves;

/// <summary>How far one hold moved between two generations.</summary>
/// <param name="DistanceMm">The displacement on the wall, mm.</param>
/// <param name="Source">Whether it was measured in 3D or estimated from the photo warp.</param>
/// <param name="RotationDeg">How far an elongated hold turned (0..90), or null when it cannot be told.</param>
public sealed record HoldMoveMeasure(double DistanceMm, HoldMoveSource Source, double? RotationDeg);

/// <summary>One carried hold pair and what its move does to the boulders.</summary>
/// <param name="OldHoldId">The old hold.</param>
/// <param name="NewHoldId">Its successor.</param>
/// <param name="Measure">The measurement.</param>
/// <param name="Outcome">The rule's verdict.</param>
public sealed record PlannedMove(Guid OldHoldId, Guid NewHoldId, HoldMoveMeasure Measure, HoldMoveOutcome Outcome);
