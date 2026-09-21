// <copyright file="CarryoverScope.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Services;

/// <summary>
/// The single definition of which old holds a carryover verdict may legitimately be made about, and the
/// guard that keeps a verdict made outside that scope from taking effect unseen.
/// <para>
/// The review draws ONE panel (the centre) and hold coordinates are panel-normalized, so only that
/// panel's old holds can be drawn, counted or stepped through. Earlier builds drew the whole old
/// generation on the centre photo, which put every other panel's holds on it as phantom circles over the
/// mats — and a <see cref="CarryKind.Removed"/> verdict on one of those phantoms is persisted as a real
/// <c>WallUpdateHoldDecision</c> row. Scoping the drawing fixed the cause but not those rows: they are
/// still restored, still shipped to the promote, and <c>FreezeRemovedBouldersAsync</c> runs before the
/// promote's old-hold guard, so they still freeze real boulders — invisibly, since nothing on screen
/// draws or counts them any more.
/// </para>
/// <para>
/// So every path into the promote runs its decisions through <see cref="Reconcile"/> first. A verdict
/// about a hold outside the reviewable set is replaced by the matcher default (carry in place, with the
/// matcher's same-panel twin) — the safe direction, and exactly what the promote's undecided-reconcile
/// would do for that hold anyway — and handed back in <see cref="CarryoverScopeResult.Reset"/> so the UI
/// can tell the user how many verdicts were reset and why. The recorded rows are deliberately NOT
/// deleted: neutralising them at the promote boundary keeps the user's work recoverable the day a panel
/// selector makes those holds reviewable again.
/// </para>
/// </summary>
public static class CarryoverScope
{
    /// <summary>
    /// The old holds a verdict may be made about: the ones on the panel the carryover review displays.
    /// That is the centre (0,0) panel — the review pins its "before" photo to it and there is no panel
    /// selector — so a hold on any other re-photographed panel is never drawn, counted or steppable.
    /// Null when the session cannot say (a pre-match staged session, which has no carried panels yet);
    /// callers then leave every decision alone rather than guessing at a scope.
    /// </summary>
    public static HashSet<Guid>? ReviewableOldHoldIds(BigUpdateSession session)
    {
        if (session.CarriedPanels is not { } panels)
        {
            return null;
        }

        var displayed = panels.FirstOrDefault(p => p is { Col: 0, Row: 0 });
        return displayed is null ? [] : displayed.OldHolds.Select(h => h.Id).ToHashSet();
    }

    /// <summary>
    /// Replaces every carry verdict made outside the reviewable scope that differs from the matcher's
    /// default with that default, and reports the ones it replaced.
    /// </summary>
    /// <param name="session">The matched session, for the reviewable scope and the matcher proposals.</param>
    /// <param name="decisions">The recorded verdicts (restored from the session, or held in memory).</param>
    /// <returns>The promote-safe decision set and the verdicts that were reset.</returns>
    public static CarryoverScopeResult Reconcile(
        BigUpdateSession session, IReadOnlyList<CarryoverDecision> decisions)
    {
        var reviewable = ReviewableOldHoldIds(session);
        if (reviewable is null)
        {
            return new CarryoverScopeResult(decisions, []);
        }

        var proposals = new Dictionary<Guid, Guid>();
        foreach (var proposal in session.Carryover)
        {
            proposals[proposal.OldHoldId] = proposal.NewHoldId;
        }

        var safe = new List<CarryoverDecision>(decisions.Count);
        var reset = new List<CarryoverDecision>();
        foreach (var decision in decisions)
        {
            if (reviewable.Contains(decision.OldHoldId))
            {
                safe.Add(decision);
                continue;
            }

            var fallback = Default(decision.OldHoldId, proposals);
            if (decision.Kind == fallback.Kind && decision.NewHoldId == fallback.NewHoldId)
            {
                safe.Add(decision);
                continue;
            }

            safe.Add(fallback);
            reset.Add(decision);
        }

        return new CarryoverScopeResult(safe, reset);
    }

    /// <summary>
    /// The matcher default for one old hold: carried in place, twinned to the matcher's same-panel
    /// proposal when there is one. Identical to what the review seeds and to what the promote's
    /// undecided-reconcile does, so resetting to it can never lose a hold or freeze a boulder.
    /// <para>
    /// Explicitly NOT confirmed: a reset throws the user's verdict away, so the hold must read as
    /// unreviewed again rather than as a phantom somebody signed off. A verdict left ALONE by
    /// <see cref="Reconcile"/> keeps whatever confirmation it had, because nothing about it changed.
    /// </para>
    /// </summary>
    private static CarryoverDecision Default(Guid oldHoldId, IReadOnlyDictionary<Guid, Guid> proposals) =>
        new(
            oldHoldId,
            CarryKind.Carried,
            proposals.TryGetValue(oldHoldId, out var twin) ? twin : null,
            Confirmed: false);
}
