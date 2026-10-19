// <copyright file="BigWallUpdate.Neighbours.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Components;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// The wizard's neighbour-overlap walk: one staged neighbour panel at a time, each confirmed outcome written to the
/// session before the walk moves on (a failed save keeps the panel on screen), and Skip never deleting earlier work.
/// </summary>
public partial class BigWallUpdate
{
    [Inject]
    private ILogger<BigWallUpdate> Logger { get; set; } = default!;

    // ---- Phase 2: one neighbour overlap at a time ------------------------------
    private async Task OnNeighbourConfirm(PanelConfirmation confirmation)
    {
        if (CurrentNeighbour is { } n)
        {
            var set = new NeighbourLinkSet(
                n.PanelId, confirmation.Links.ToList(), confirmation.RemovedNeighborHoldIds.ToList());

            // STAY on the panel when the save failed, exactly like the carryover step: the promote reads the session,
            // so advancing past a lost save dropped this panel's links while the summary still counted them.
            if (!await SaveNeighbourAsync(set))
            {
                neighbourStepper?.FinishFailed();
                return;
            }

            RecordLinkSet(set);
        }

        await AdvanceNeighbourAsync();
    }

    /// <summary>
    /// The stepper's as-you-go save: the panel's outcome so far, written before it is finished, so an
    /// interrupted walk through one panel's proposals comes back with the confirmations already made.
    /// The set replaces the panel's rows whole, exactly as the final confirm does.
    /// </summary>
    private async Task OnNeighbourProgress(PanelConfirmation confirmation)
    {
        if (CurrentNeighbour is not { } n)
        {
            return;
        }

        await SaveNeighbourAsync(new NeighbourLinkSet(
            n.PanelId, confirmation.Links.ToList(), confirmation.RemovedNeighborHoldIds.ToList()));
    }

    /// <summary>
    /// Skipping a neighbour keeps its panel (it is promoted with the rest) but records no links — it
    /// never deletes the staged panel, so no holds are lost.
    /// <para>
    /// Skip means "not now", never "throw away what I already decided". A panel confirmed in an EARLIER
    /// session keeps its rows: clearing them here destroyed real, already-confirmed work with no prompt,
    /// and because the in-memory set was emptied to match, the screen agreed and the loss was invisible.
    /// Preserving beats confirming-first because the destructive reading of Skip has no use case — a user
    /// who wants a panel's links gone re-walks it and confirms an empty set, which rewrites its rows.
    /// </para>
    /// </summary>
    private async Task OnNeighbourSkip()
    {
        if (CurrentNeighbour is { } n)
        {
            // Only a panel with NOTHING recorded gets an empty set; anything already decided is left
            // exactly as it stands, in memory and in the session.
            var existing = _linkSets.FirstOrDefault(l => l.PanelId == n.PanelId) ?? RestoredFor(n.PanelId);
            RecordLinkSet(existing ?? new NeighbourLinkSet(n.PanelId, [], []));
        }

        await AdvanceNeighbourAsync();
    }

    // A panel is always decided WHOLE, so a re-walk replaces its set rather than appending a second one.
    private void RecordLinkSet(NeighbourLinkSet set)
    {
        _linkSets.RemoveAll(l => l.PanelId == set.PanelId);
        _linkSets.Add(set);
    }

    /// <summary>Writes one panel's outcome to the session; false (with the reason shown) when it was not saved.</summary>
    private async Task<bool> SaveNeighbourAsync(NeighbourLinkSet set)
    {
        try
        {
            await Sessions.SaveNeighbourLinkSetAsync(WallId, set, _sessionInfo?.Id);
            _error = null;
            return true;
        }
        catch (WallUpdateSessionSupersededException)
        {
            MarkSuperseded();
            return false;
        }
        catch (Exception ex)
        {
            _error = "Could not save this panel's overlaps, so this panel is not finished yet: "
                + UpdateErrorText.Describe(ex, Logger, "saving a neighbour panel's overlaps");
            return false;
        }
    }

    private async Task AdvanceNeighbourAsync()
    {
        if (_session is not null && _neighbourIndex < _session.Neighbours.Count - 1)
        {
            _neighbourIndex++;
            await GoToPhaseAsync(WallUpdatePhase.Neighbours, _neighbourIndex);
        }
        else
        {
            await GoToPhaseAsync(WallUpdatePhase.Touchup);
        }
    }
}
