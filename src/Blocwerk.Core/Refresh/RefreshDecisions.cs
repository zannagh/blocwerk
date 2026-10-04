// <copyright file="RefreshDecisions.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Security.Cryptography;
using System.Text;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;

namespace Blocwerk.Core.Refresh;

/// <summary>
/// What Apply would promote right now: the update session's decisions run through <see cref="CarryoverScope"/> against
/// the matched session (exactly as Apply does), with the accepted "this hold moved" suggestions folded in (as the promote
/// does). Its <see cref="PromotableDecisions.Version"/> covers those decisions and the staged holds they are about (a hold
/// added or deleted in the touch-up or the wall editor while the update is staged changes what is promoted), is stored with the summary the confirm screen shows, and Apply
/// promotes only when the decisions still have that version: the user applies what they checked, or is shown the new
/// summary first.
/// </summary>
internal static class RefreshDecisions
{
    /// <summary>Reads the session's decisions and works out what Apply would promote with <paramref name="matched"/>.</summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="actors">The services acting as the run's starter.</param>
    /// <param name="matched">The matched session.</param>
    /// <param name="stagedByPanel">The staged holds per staged panel, as they are now.</param>
    /// <returns>What Apply would promote, with its version.</returns>
    public static async Task<PromotableDecisions> LoadAsync(
        Guid wallId, WallRefreshActors actors, BigUpdateSession matched, IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> stagedByPanel)
    {
        var decisions = await actors.Sessions.GetDecisionsAsync(wallId);
        var scoped = CarryoverScope.Reconcile(matched, decisions.Carryover);
        var promotable = decisions with { Carryover = scoped.Decisions.ToList() };
        var relocations = await actors.Sessions.GetRelocationSuggestionsAsync(wallId);
        var accepted = relocations
            .Where(r => RelocationFold.VerdictOf(r.Status) is not null)
            .Select(r => (r.OldHoldId, r.NewHoldId, RelocationFold.VerdictOf(r.Status)!.Value))
            .ToList();
        var exceptions = await actors.Sessions.GetUpdateExceptionsAsync(wallId);
        var review = ReviewOldHoldIds(exceptions);
        promotable = promotable with
        {
            ReviewOldHoldIds = review,
            HandPlacedMergeOldIds = (matched.HandPlacedMerges ?? []).Select(m => m.OldHoldId).ToList(),
        };
        var folded = RelocationFold.Apply(promotable, accepted);
        var moves = await actors.BigUpdate.PreviewHoldMovesAsync(
            wallId, folded with { CarriedWarpPositions = matched.CarriedWarpPositions });
        var linked = folded.Neighbours.Sum(n => n.Links.Count);
        var leftOut = Math.Max(0, matched.Neighbours.Sum(n => n.Proposals.Count) - linked);
        return new PromotableDecisions(
            promotable,
            folded,
            scoped.Reset.Count,
            relocations.Count(r => r.Status == RelocationProposalStatus.Pending),
            leftOut,
            Fingerprint(folded, stagedByPanel, moves.Version),
            exceptions,
            moves);
    }

    /// <summary>The old holds of unanswered cards: they go live as decided, marked for review.</summary>
    /// <param name="exceptions">The session's cards.</param>
    /// <returns>The old hold ids.</returns>
    public static IReadOnlyCollection<Guid> ReviewOldHoldIds(IReadOnlyList<UpdateExceptionInfo> exceptions) =>
        exceptions
            .Where(e => e.Status == UpdateExceptionStatus.Pending
                && e.Kind is UpdateExceptionKind.PossiblyRemoved or UpdateExceptionKind.LowConfidenceMatch or UpdateExceptionKind.HandPlacedAmbiguous)
            .Select(e => e.OldHoldId)
            .OfType<Guid>()
            .ToHashSet();

    /// <summary>The summary with the version of <paramref name="decisions"/> and its cards counted.</summary>
    /// <param name="summary">The counts.</param>
    /// <param name="decisions">What Apply would promote.</param>
    /// <returns>The summary.</returns>
    public static RefreshSummary Stamp(RefreshSummary summary, PromotableDecisions decisions) =>
        summary with
        {
            DecisionsVersion = decisions.Version,
            PossiblyRemovedHolds = decisions.Exceptions.Count(e => e.Kind == UpdateExceptionKind.PossiblyRemoved),
            ChecksOpen = decisions.Exceptions.Count(e => e.Status == UpdateExceptionStatus.Pending),
        };

    /// <summary>The decisions in the shape the summary is worked out from.</summary>
    public static QuickDecisions AsQuick(PromotableDecisions decisions) =>
        new(
            decisions.Folded.Carryover,
            decisions.Folded.AcceptedNewCenterHoldIds,
            decisions.Folded.RemovedNewCenterHoldIds,
            decisions.Folded.Neighbours,
            decisions.OverlapsLeftOut);

    /// <summary>A short, order-independent fingerprint of everything that decides what the promote does.</summary>
    /// <param name="c">The decisions (relocations folded in).</param>
    /// <param name="stagedByPanel">The staged holds per staged panel.</param>
    /// <returns>The fingerprint.</returns>
    public static string Fingerprint(
        BigUpdateConfirmation c, IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> stagedByPanel, string? movesVersion = null)
    {
        var text = new StringBuilder();
        text.Append("m:").Append(movesVersion).Append('\n');
        foreach (var (panel, holds) in stagedByPanel.OrderBy(p => p.Key.ToString(), StringComparer.Ordinal))
        {
            text.Append("s:").Append(panel).Append('\n');
            Append(text, "h", holds);
        }

        foreach (var d in c.Carryover.Select(d => $"c:{d.OldHoldId}:{d.Kind}:{d.NewHoldId}:{d.Confirmed}").Order(StringComparer.Ordinal))
        {
            text.Append(d).Append('\n');
        }

        Append(text, "a", c.AcceptedNewCenterHoldIds);
        Append(text, "r", c.RemovedNewCenterHoldIds);
        foreach (var n in c.Neighbours.OrderBy(n => n.PanelId.ToString(), StringComparer.Ordinal))
        {
            text.Append("n:").Append(n.PanelId).Append('\n');
            Append(text, "l", n.Links.Select(l => $"{l.NeighborHoldId}>{l.NewHoldId}:{l.Moved}"));
            Append(text, "x", n.RemovedNeighbourHoldIds);
        }

        Append(text, "f", c.ReviewOldHoldIds ?? []);
        Append(text, "p", c.HandPlacedMergeOldIds ?? []);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()));
        return Convert.ToHexString(hash, 0, 12);
    }

    private static void Append<T>(StringBuilder text, string tag, IEnumerable<T> values)
    {
        foreach (var value in values.Select(v => v?.ToString() ?? string.Empty).Order(StringComparer.Ordinal))
        {
            text.Append(tag).Append(':').Append(value).Append('\n');
        }
    }
}

/// <summary>What Apply would promote, see <see cref="RefreshDecisions"/>.</summary>
/// <param name="Scoped">The session's decisions after <see cref="CarryoverScope.Reconcile"/>: what is handed to the promote.</param>
/// <param name="Folded"><paramref name="Scoped"/> with the accepted relocations folded in: what the promote ends up doing.</param>
/// <param name="Resets">Verdicts outside the reviewable scope that Apply resets to the matcher's default.</param>
/// <param name="PendingRelocations">"This hold may have moved" suggestions nobody decided.</param>
/// <param name="OverlapsLeftOut">Overlap suggestions not linked.</param>
/// <param name="Version">The fingerprint of <paramref name="Folded"/>.</param>
/// <param name="Exceptions">The confirm screen's cards (their unanswered old holds are in <paramref name="Folded"/>).</param>
/// <param name="Moves">The carried holds that physically moved and what that does to their boulders; part of <paramref name="Version"/>.</param>
internal sealed record PromotableDecisions(
    BigUpdateConfirmation Scoped,
    BigUpdateConfirmation Folded,
    int Resets,
    int PendingRelocations,
    int OverlapsLeftOut,
    string Version,
    IReadOnlyList<UpdateExceptionInfo> Exceptions,
    HoldMoves.HoldMovePlan Moves);
