// <copyright file="RelocationSuggestion.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Services;

/// <summary>One "this hold moved" suggestion of the open wall-update session, as the review shows it.</summary>
/// <param name="Id">The suggestion's id (what <see cref="IWallUpdateSessionService.DecideRelocationAsync"/> takes).</param>
/// <param name="OldHoldId">The old live hold that disappeared.</param>
/// <param name="NewHoldId">The staged hold proposed to be it, moved.</param>
/// <param name="Score">Fingerprint similarity, 0..1.</param>
/// <param name="Margin">Lead over the best competing pair.</param>
/// <param name="Metric">True when millimetre sizes took part; false = colour and shape only (lower confidence).</param>
/// <param name="Status">Pending, accepted or dismissed.</param>
public sealed record RelocationSuggestion(
    Guid Id,
    Guid OldHoldId,
    Guid NewHoldId,
    double Score,
    double Margin,
    bool Metric,
    RelocationProposalStatus Status);
