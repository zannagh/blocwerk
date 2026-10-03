// <copyright file="FailingDecisionSessions.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;

namespace Blocwerk.Core.Tests.Refresh;

/// <summary>The real session service, except that saving the carryover decisions fails (a step after staging).</summary>
internal sealed class FailingDecisionSessions(IWallUpdateSessionService inner) : IWallUpdateSessionService
{
    public Task<WallUpdateSessionInfo?> GetOpenSessionAsync(Guid wallId) => inner.GetOpenSessionAsync(wallId);

    public Task<WallUpdateSessionInfo> SetPhaseAsync(Guid wallId, WallUpdatePhase phase, int neighbourIndex = 0) =>
        inner.SetPhaseAsync(wallId, phase, neighbourIndex);

    public Task<BigUpdateConfirmation> GetDecisionsAsync(Guid wallId) => inner.GetDecisionsAsync(wallId);

    public Task SaveCarryDecisionAsync(Guid wallId, CarryoverDecision decision) => inner.SaveCarryDecisionAsync(wallId, decision);

    public Task<IReadOnlyList<CarryConfirmation>> GetCarryConfirmationsAsync(Guid wallId) => inner.GetCarryConfirmationsAsync(wallId);

    public Task ClearCarryConfirmationAsync(Guid wallId, Guid oldHoldId) => inner.ClearCarryConfirmationAsync(wallId, oldHoldId);

    public Task SaveNewCentreHoldDecisionAsync(Guid wallId, Guid stagedHoldId, bool discarded) =>
        inner.SaveNewCentreHoldDecisionAsync(wallId, stagedHoldId, discarded);

    public Task SaveCarryOutcomeAsync(
        Guid wallId, IReadOnlyList<CarryoverDecision> carryover, IReadOnlyList<Guid> acceptedNewCentreHoldIds, IReadOnlyList<Guid> removedNewCentreHoldIds) =>
        throw new InvalidOperationException("The decisions could not be saved.");

    public Task SaveNeighbourLinkSetAsync(Guid wallId, NeighbourLinkSet linkSet) => inner.SaveNeighbourLinkSetAsync(wallId, linkSet);

    public Task<DateTimeOffset?> SaveDefaultDecisionsAsync(Guid wallId, DefaultDecisions decisions, DateTimeOffset? onlyIfUnchangedSince = null) =>
        throw new InvalidOperationException("The decisions could not be saved.");

    public Task<IReadOnlyList<RelocationSuggestion>> GetRelocationSuggestionsAsync(Guid wallId) => inner.GetRelocationSuggestionsAsync(wallId);

    public Task DecideRelocationAsync(Guid wallId, Guid suggestionId, RelocationDecision decision) =>
        inner.DecideRelocationAsync(wallId, suggestionId, decision);
}
