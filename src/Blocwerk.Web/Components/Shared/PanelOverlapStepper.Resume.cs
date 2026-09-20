// <copyright file="PanelOverlapStepper.Resume.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Components;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// The stepper's durability seam: seeding it from a panel outcome that was already confirmed (so a
/// resumed big-wall update comes back to the panel with its links and removals in place) and reporting
/// the panel's outcome-so-far outward as the user steps, so an interrupted walk through one panel does
/// not start again from its first proposal.
/// <para>
/// Both are opt-in: the add-panel flow, which has no update session behind it, simply leaves
/// <see cref="Restored"/> null and <see cref="OnProgress"/> unwired and behaves exactly as before.
/// </para>
/// </summary>
public partial class PanelOverlapStepper
{
    /// <summary>
    /// This panel's previously confirmed outcome, when the flow is being resumed. Laid over the fresh
    /// proposal walk so the user sees what they decided rather than an untouched stepper.
    /// </summary>
    [Parameter] public NeighbourLinkSet? Restored { get; set; }

    /// <summary>
    /// Raised with the panel's outcome so far after every decision, so the host can persist it before
    /// the panel is finished. Unwired where there is nothing to persist to.
    /// </summary>
    [Parameter] public EventCallback<PanelConfirmation> OnProgress { get; set; }

    /// <summary>
    /// Replays a restored outcome onto this walk: a link whose neighbour hold is one of the proposals
    /// re-occupies that proposal's slot (whichever new hold it ended up pointing at, so a "moved" pick
    /// survives), and anything else becomes a standalone pairing. Removals come back wholesale.
    /// <para>
    /// NO restored link is ever dropped. The slot for a proposal can already be taken — two confirmed
    /// links may share a neighbour hold, since only the NEW hold end is guarded against reuse — and the
    /// earlier shape silently discarded the second one, un-confirming work the user had already done and
    /// persisted. Whatever cannot take a proposal slot goes in <c>_manualLinks</c> instead, which is
    /// where a link with no proposal behind it belongs anyway; the taken-ness checks read that list too,
    /// so a restored link still blocks its new hold from being claimed twice.
    /// </para>
    /// </summary>
    private void SeedFromRestored()
    {
        if (Restored is not { } restored)
        {
            return;
        }

        foreach (var holdId in restored.RemovedNeighbourHoldIds)
        {
            _removed.Add(holdId);
        }

        foreach (var link in restored.Links)
        {
            var index = _steps.FindIndex(p => p.HoldAId == link.NeighborHoldId);
            if (index >= 0 && _decisions[index] is null)
            {
                _decisions[index] = link;
            }
            else
            {
                _manualLinks.Add(link);
            }
        }
    }

    /// <summary>The panel's outcome as it stands — the same shape the final confirm hands over.</summary>
    private PanelConfirmation CurrentOutcome()
    {
        var links = _decisions
            .Where(d => d is not null)
            .Select(d => d!)
            .Concat(_manualLinks)
            .Where(d => !_removed.Contains(d.NeighborHoldId))
            .ToList();
        return new PanelConfirmation(links, _removed.ToList());
    }

    /// <summary>
    /// Writes the panel's outcome so far outward. Called on decisions, not on renders or navigation:
    /// each one is a circuit round-trip, and stepping back and forth changes nothing to save.
    /// </summary>
    private async Task ReportProgressAsync()
    {
        if (!OnProgress.HasDelegate)
        {
            return;
        }

        await OnProgress.InvokeAsync(CurrentOutcome());
    }
}
