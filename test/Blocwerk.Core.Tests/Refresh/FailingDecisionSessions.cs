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

    public Task<WallUpdateSessionInfo> SetPhaseAsync(Guid wallId, WallUpdatePhase phase, int neighbourIndex = 0, Guid? expectedSessionId = null) =>
        inner.SetPhaseAsync(wallId, phase, neighbourIndex, expectedSessionId);

    public Task<BigUpdateConfirmation> GetDecisionsAsync(Guid wallId) => inner.GetDecisionsAsync(wallId);

    public Task SaveCarryDecisionAsync(Guid wallId, CarryoverDecision decision, Guid? expectedSessionId = null) =>
        inner.SaveCarryDecisionAsync(wallId, decision, expectedSessionId);

    public Task<IReadOnlyList<CarryConfirmation>> GetCarryConfirmationsAsync(Guid wallId) => inner.GetCarryConfirmationsAsync(wallId);

    public Task ClearCarryConfirmationAsync(Guid wallId, Guid oldHoldId, Guid? expectedSessionId = null) =>
        inner.ClearCarryConfirmationAsync(wallId, oldHoldId, expectedSessionId);

    public Task SaveNewCentreHoldDecisionAsync(Guid wallId, Guid stagedHoldId, bool discarded, Guid? expectedSessionId = null) =>
        inner.SaveNewCentreHoldDecisionAsync(wallId, stagedHoldId, discarded, expectedSessionId);

    public Task SaveCarryOutcomeAsync(
        Guid wallId,
        IReadOnlyList<CarryoverDecision> carryover,
        IReadOnlyList<Guid> acceptedNewCentreHoldIds,
        IReadOnlyList<Guid> removedNewCentreHoldIds,
        Guid? expectedSessionId = null) =>
        throw new InvalidOperationException("The decisions could not be saved.");

    public Task SaveNeighbourLinkSetAsync(Guid wallId, NeighbourLinkSet linkSet, Guid? expectedSessionId = null) =>
        inner.SaveNeighbourLinkSetAsync(wallId, linkSet, expectedSessionId);

    public Task<DateTimeOffset?> SaveDefaultDecisionsAsync(
        Guid wallId, DefaultDecisions decisions, DateTimeOffset? onlyIfUnchangedSince = null, Guid? expectedSessionId = null) =>
        throw new InvalidOperationException("The decisions could not be saved.");

    public Task<IReadOnlyList<RelocationSuggestion>> GetRelocationSuggestionsAsync(Guid wallId) => inner.GetRelocationSuggestionsAsync(wallId);

    public Task DecideRelocationAsync(Guid wallId, Guid suggestionId, RelocationDecision decision, Guid? expectedSessionId = null) =>
        inner.DecideRelocationAsync(wallId, suggestionId, decision, expectedSessionId);

    public Task<IReadOnlyList<UpdateExceptionInfo>> GetUpdateExceptionsAsync(Guid wallId) => inner.GetUpdateExceptionsAsync(wallId);

    public Task DecideUpdateExceptionAsync(Guid wallId, Guid exceptionId, UpdateExceptionAnswer answer, Guid? expectedSessionId = null) =>
        inner.DecideUpdateExceptionAsync(wallId, exceptionId, answer, expectedSessionId);
}
