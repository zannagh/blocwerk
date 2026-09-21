// <copyright file="CarryConfirmationPolicy.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Services;

/// <summary>
/// The one place that decides what a write does to a carry decision's human-confirmation fact, so the
/// incremental upsert and the bulk carryover rewrite can never disagree about it.
/// <para>
/// The rules, in order:
/// a write carrying <c>confirmed: true</c> confirms (recording who and when), and re-confirming an
/// already-confirmed, unchanged verdict is a no-op so the attribution does not churn;
/// a write carrying <c>confirmed: false</c> — the SEED, the matcher suggestion, the bulk replay of the
/// whole carryover half — leaves an existing confirmation alone as long as the verdict it was given
/// about is unchanged;
/// and a write that CHANGES the verdict without confirming it clears the confirmation, because the
/// human sign-off was about the verdict that just went away. That last rule is what makes the
/// <see cref="CarryoverScope"/> reset — which replaces an out-of-scope verdict with the matcher default
/// — read as unreviewed rather than as someone's reviewed work.
/// </para>
/// </summary>
public static class CarryConfirmationPolicy
{
    /// <summary>
    /// Writes a carry verdict onto <paramref name="row"/> and folds the confirmation intent in.
    /// </summary>
    /// <param name="row">The decision row, still holding its previous verdict and confirmation.</param>
    /// <param name="kind">The verdict being written.</param>
    /// <param name="pairedHoldId">The staged twin being written (already null for a removal).</param>
    /// <param name="confirmed">True when this write is a deliberate human confirmation.</param>
    /// <param name="userId">The admin performing the write, recorded when it confirms.</param>
    /// <param name="now">The write timestamp, recorded when it confirms.</param>
    public static void Apply(
        WallUpdateHoldDecision row,
        CarryKind kind,
        Guid? pairedHoldId,
        bool confirmed,
        Guid userId,
        DateTimeOffset now)
    {
        var verdictChanged = row.CarryKind != kind || row.PairedHoldId != pairedHoldId;
        row.CarryKind = kind;
        row.PairedHoldId = pairedHoldId;

        if (confirmed)
        {
            if (!row.Confirmed || verdictChanged)
            {
                row.Confirmed = true;
                row.ConfirmedByUserId = userId;
                row.ConfirmedAt = now;
            }

            return;
        }

        if (verdictChanged)
        {
            Clear(row);
        }
    }

    /// <summary>Drops the confirmation entirely, so the row reads as the matcher's unreviewed default.</summary>
    public static void Clear(WallUpdateHoldDecision row)
    {
        row.Confirmed = false;
        row.ConfirmedByUserId = null;
        row.ConfirmedAt = null;
    }

    /// <summary>Copies a previous row's verdict and confirmation onto a replacement row before applying a write.</summary>
    public static void CopyFrom(WallUpdateHoldDecision row, WallUpdateHoldDecision previous)
    {
        row.CarryKind = previous.CarryKind;
        row.PairedHoldId = previous.PairedHoldId;
        row.Confirmed = previous.Confirmed;
        row.ConfirmedByUserId = previous.ConfirmedByUserId;
        row.ConfirmedAt = previous.ConfirmedAt;
    }
}
