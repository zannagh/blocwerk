// <copyright file="DefaultDecisions.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Services;

/// <summary>A complete set of decisions for an update session, written at once (<see cref="IWallUpdateSessionService.SaveDefaultDecisionsAsync"/>).</summary>
/// <param name="Carryover">One verdict per old hold.</param>
/// <param name="AcceptedNewCentreHoldIds">New centre holds kept.</param>
/// <param name="RemovedNewCentreHoldIds">Centre detections dropped.</param>
/// <param name="Neighbours">Per neighbour panel: links and removed detections.</param>
/// <param name="Phase">The phase the session moves to.</param>
public sealed record DefaultDecisions(
    IReadOnlyList<CarryoverDecision> Carryover,
    IReadOnlyList<Guid> AcceptedNewCentreHoldIds,
    IReadOnlyList<Guid> RemovedNewCentreHoldIds,
    IReadOnlyList<NeighbourLinkSet> Neighbours,
    WallUpdatePhase Phase);
