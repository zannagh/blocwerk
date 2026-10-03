// <copyright file="WallUpdateSessionService.Relocations.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Services;

/// <summary>
/// The "this hold moved" suggestions of the session: reading them, and the moved / same-hold / dismiss
/// answers. An accept is written as the very carry verdict a person would record by hand — Changed
/// ("moved here") or Carried ("same hold"), pointed at the new hold, confirmed — so every later step (the
/// review's counts, the bulk save, the promote) treats it exactly like a manual mark or a normal match.
/// </summary>
public partial class WallUpdateSessionService
{
    /// <inheritdoc/>
    public async Task<IReadOnlyList<RelocationSuggestion>> GetRelocationSuggestionsAsync(Guid wallId)
    {
        var user = await currentUserService.GetCurrentUserAsync();
        await using var db = await dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = user.Id;
        await WallAdminGuard.EnsureWallAdminAsync(db, wallId, user.Id, CancellationToken.None);

        var session = await WallUpdateSessions.FindOpenAsync(db, wallId);
        if (session is null)
        {
            return [];
        }

        var rows = await db.WallUpdateRelocationProposals
            .Where(p => p.SessionId == session.Id)
            .ToListAsync();
        return rows
            .OrderByDescending(p => p.Score)
            .Select(p => new RelocationSuggestion(p.Id, p.OldHoldId, p.NewHoldId, p.Score, p.Margin, p.Metric, p.Status))
            .ToList();
    }

    /// <inheritdoc/>
    public async Task DecideRelocationAsync(Guid wallId, Guid suggestionId, RelocationDecision decision)
    {
        var user = await currentUserService.GetCurrentUserAsync();
        await using var db = await dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = user.Id;
        await WallAdminGuard.EnsureWallAdminAsync(db, wallId, user.Id, CancellationToken.None);

        await using var transaction = await db.Database.BeginTransactionAsync();
        var session = await RequireOpenAsync(db, wallId);
        await LockSessionAsync(db, session.Id);
        var proposal = await db.WallUpdateRelocationProposals
            .FirstOrDefaultAsync(p => p.Id == suggestionId && p.SessionId == session.Id)
            ?? throw new InvalidOperationException("That suggestion is no longer part of this update.");
        var row = await db.WallUpdateHoldDecisions.FirstOrDefaultAsync(d =>
            d.SessionId == session.Id && d.Kind == WallUpdateHoldDecisionKind.Carry && d.HoldId == proposal.OldHoldId);
        var now = DateTimeOffset.UtcNow;

        if (decision == RelocationDecision.Dismiss)
        {
            UndoAcceptedVerdict(proposal, row, user.Id, now);
            proposal.Status = RelocationProposalStatus.Dismissed;
        }
        else
        {
            await EnsureNewHoldUnclaimedAsync(db, session.Id, proposal);
            if (row is null)
            {
                row = new WallUpdateHoldDecision
                {
                    SessionId = session.Id, Kind = WallUpdateHoldDecisionKind.Carry, HoldId = proposal.OldHoldId,
                };
                db.WallUpdateHoldDecisions.Add(row);
            }

            var sameHold = decision == RelocationDecision.SameHold;
            var kind = sameHold ? CarryKind.Carried : CarryKind.Changed;
            CarryConfirmationPolicy.Apply(row, kind, proposal.NewHoldId, confirmed: true, user.Id, now);
            row.UpdatedAt = now;
            proposal.Status = sameHold ? RelocationProposalStatus.AcceptedAsSame : RelocationProposalStatus.Accepted;
        }

        proposal.DecidedByUserId = user.Id;
        proposal.DecidedAt = now;
        WallUpdateSessions.Touch(session, user.Id);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    /// <summary>
    /// The claim check reads, then writes. Taking the session row's write lock first (an UPDATE held
    /// to commit) serialises concurrent decisions on the session, so the check sees every verdict
    /// committed before it and two accepts cannot both claim one new hold.
    /// </summary>
    private static Task LockSessionAsync(BlocwerkDbContext db, Guid sessionId) =>
        db.WallUpdateSessions.Where(s => s.Id == sessionId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow));

    /// <summary>
    /// Undoes an accept only while the carry verdict is still the one that accept wrote (its kind, pointed
    /// at the suggestion's new hold); a verdict the user has since changed by hand is theirs and stays.
    /// </summary>
    private static void UndoAcceptedVerdict(
        WallUpdateRelocationProposal proposal, WallUpdateHoldDecision? row, Guid userId, DateTimeOffset now)
    {
        if (RelocationFold.VerdictOf(proposal.Status) is not { } written
            || row is null || row.CarryKind != written || row.PairedHoldId != proposal.NewHoldId)
        {
            return;
        }

        CarryConfirmationPolicy.Apply(row, CarryKind.Carried, null, confirmed: false, userId, now);
        row.UpdatedAt = now;
    }

    /// <summary>
    /// Refuses an accept (either answer) whose new hold another old hold's verdict already points at: accepting it would
    /// turn a move into a silent physical merge of two old holds.
    /// </summary>
    private static async Task EnsureNewHoldUnclaimedAsync(
        BlocwerkDbContext db, Guid sessionId, WallUpdateRelocationProposal proposal)
    {
        var claimed = await db.WallUpdateHoldDecisions.AnyAsync(d =>
            d.SessionId == sessionId
            && d.Kind == WallUpdateHoldDecisionKind.Carry
            && d.HoldId != proposal.OldHoldId
            && d.CarryKind != CarryKind.Removed
            && d.PairedHoldId == proposal.NewHoldId);
        if (claimed)
        {
            throw new InvalidOperationException("Another old hold is already carried onto that new hold.");
        }
    }
}
