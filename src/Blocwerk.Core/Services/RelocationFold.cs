// <copyright file="RelocationFold.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Services;

/// <summary>
/// Folds the ACCEPTED "this hold moved" suggestions into a promote payload, so the promote honours them
/// even when the carry verdict the accept wrote was lost or overwritten by a stale bulk save. Pure.
/// <para>
/// A pair accepted as MOVED (old → new) becomes the carry verdict <c>Changed, twin = new</c> — exactly what
/// a person marking the hold "changed" and pointing it at the new detection records (decision D-A: moved
/// == changed): the staged hold goes live in place with the old hold's curated fields, a Changed
/// <see cref="Entities.HoldGenerationLink"/> ties them, and every boulder that used the hold is flagged for
/// review. A pair accepted as the SAME HOLD becomes <c>Carried, twin = new</c> — exactly a normal
/// confirmed match: promoted in place, curation copied, linked Same, boulders re-pointed, nothing flagged.
/// The old row is retained as history either way; nothing is deleted.
/// </para>
/// <para>
/// A later, deliberate verdict wins over the accept: an old hold now marked Removed, or pointed at a
/// DIFFERENT new hold, is left alone, and so is an accept whose new hold another old hold has since
/// claimed (that would silently turn a move into a merge).
/// </para>
/// </summary>
public static class RelocationFold
{
    /// <summary>
    /// The carry verdict an accepted suggestion stands for: Changed for "moved here", Carried for "same
    /// hold", none for a pending or dismissed one.
    /// </summary>
    /// <param name="status">The suggestion's status.</param>
    /// <returns>The carry kind the accept wrote, or null when it is not accepted.</returns>
    public static CarryKind? VerdictOf(RelocationProposalStatus status) => status switch
    {
        RelocationProposalStatus.Accepted => CarryKind.Changed,
        RelocationProposalStatus.AcceptedAsSame => CarryKind.Carried,
        _ => null,
    };

    /// <summary>Applies the accepted pairs to <paramref name="confirmation"/>.</summary>
    /// <param name="confirmation">The payload being promoted.</param>
    /// <param name="accepted">The session's accepted pairs (old hold id, new hold id, the accept's verdict).</param>
    /// <returns>The payload with every honourable accepted pair as its carry verdict.</returns>
    public static BigUpdateConfirmation Apply(
        BigUpdateConfirmation confirmation,
        IReadOnlyList<(Guid OldHoldId, Guid NewHoldId, CarryKind Kind)> accepted)
    {
        if (accepted.Count == 0)
        {
            return confirmation;
        }

        var decisions = confirmation.Carryover.ToList();
        foreach (var (oldHoldId, newHoldId, kind) in accepted)
        {
            if (ClaimedByAnother(decisions, oldHoldId, newHoldId))
            {
                continue;
            }

            var index = decisions.FindIndex(d => d.OldHoldId == oldHoldId);
            if (index < 0)
            {
                decisions.Add(new CarryoverDecision(oldHoldId, kind, newHoldId, Confirmed: true));
                continue;
            }

            var current = decisions[index];
            if (current.Kind == CarryKind.Removed || (current.NewHoldId is { } twin && twin != newHoldId))
            {
                continue;
            }

            decisions[index] = current with { Kind = kind, NewHoldId = newHoldId };
        }

        return confirmation with { Carryover = decisions };
    }

    private static bool ClaimedByAnother(List<CarryoverDecision> decisions, Guid oldHoldId, Guid newHoldId) =>
        decisions.Any(d => d.OldHoldId != oldHoldId && d.Kind != CarryKind.Removed && d.NewHoldId == newHoldId);
}
