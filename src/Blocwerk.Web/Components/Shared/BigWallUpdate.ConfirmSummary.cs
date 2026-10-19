// <copyright file="BigWallUpdate.ConfirmSummary.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// The confirm step's counts. They come from the SESSION, read when the confirm step is shown, because that is what
/// Apply promotes: counting this circuit's memory of the carryover let the chips say "1 changed" for a verdict a later
/// step (a delete in the final touch-up) had already taken out of the session, or another admin had changed.
/// </summary>
public partial class BigWallUpdate
{
    /// <summary>The session's decisions as last read for the confirm step (or as promoted); null before that.</summary>
    private BigUpdateConfirmation? confirmDecisions;

    private IReadOnlyList<CarryoverDecision> SummaryCarryover => confirmDecisions?.Carryover ?? _outcome?.Carryover ?? [];

    private int CarriedCount => SummaryCarryover.Count(d => d.Kind == CarryKind.Carried);

    private int ChangedCount => SummaryCarryover.Count(d => d.Kind == CarryKind.Changed);

    private int RemovedCount => SummaryCarryover.Count(d => d.Kind == CarryKind.Removed);

    private int NewKeptCount => confirmDecisions?.AcceptedNewCenterHoldIds.Count ?? _outcome?.AcceptedNewCenterHoldIds.Count ?? 0;

    private int LinkCount => (confirmDecisions?.Neighbours ?? _linkSets).Sum(l => l.Links.Count);

    /// <summary>Everything the confirm step shows: the session's decisions (scope-reconciled, as Apply sends them) and the shape tally.</summary>
    private async Task LoadConfirmSummaryAsync()
    {
        await LoadConfirmDecisionsAsync();
        await LoadShapeSummaryAsync();
    }

    /// <summary>
    /// Reads the decisions the promote will read. A legacy staged update has no session, so its counts stay on this
    /// circuit's memory, which is all there is for it; a failed read does too, and Apply re-reads the session anyway.
    /// </summary>
    private async Task LoadConfirmDecisionsAsync()
    {
        if (_sessionInfo is null)
        {
            confirmDecisions = null;
            return;
        }

        try
        {
            var persisted = await Sessions.GetDecisionsAsync(WallId);
            var scoped = ReconcileCarryScope(persisted.Carryover);
            RecordScopeResets(scoped.Reset);
            confirmDecisions = persisted with { Carryover = scoped.Decisions.ToList() };
        }
        catch (Exception)
        {
            confirmDecisions = null;
        }
    }
}
