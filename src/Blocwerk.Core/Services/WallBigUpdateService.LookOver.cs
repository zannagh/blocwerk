// <copyright file="WallBigUpdateService.LookOver.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.LookOver;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

/// <summary>
/// What a promote leaves for the wall look-over: the findings nobody answered are kept with the wall
/// (<see cref="WallLookOverFinding"/>) and the wall is flagged until a wall admin or moderator has looked it over.
/// Every promote comes through here, whoever started it (the confirm screen, the API, the step-by-step review).
/// </summary>
public partial class WallBigUpdateService
{
    /// <summary>Gets or sets what works the findings out of a promote's facts (replaced in tests to make it fail).</summary>
    internal Func<LookOverPromoteFacts, List<WallLookOverFinding>> LookOverBuilder { get; set; } = LookOverFindingBuilder.Build;

    /// <summary>
    /// Retires what was open on the re-photographed panels, adds this promote's findings and flags the wall when there
    /// are any. Runs last, on the promote's own context before it saves, so it reads what the promote did from the
    /// change tracker and commits with it. Neither half can fail the promote, and the first does not depend on the
    /// second: a finding about a hold that just became history must never stay answerable.
    /// </summary>
    private async Task RecordLookOverAsync(BlocwerkDbContext db, Wall wall, int newGen, LookOverPromoteInputs inputs)
    {
        await SupersedeLookOverAsync(db, wall.Id, inputs.ReplacedPanelIds);
        KeepDiscardedDetections(db, wall, newGen, await LeftOutByAPersonAsync(db, wall.Id));
        try
        {
            var session = await WallUpdateSessions.FindOpenAsync(db, wall.Id);
            var cards = session is null
                ? []
                : await db.WallUpdateExceptions.AsNoTracking().Where(e => e.SessionId == session.Id).ToListAsync();
            var unaligned = session?.UnalignedCarryPanelIds.ToHashSet() ?? [];
            var findings = LookOverBuilder(ReadPromoteFacts(db, wall.Id, newGen, cards, unaligned, inputs));
            findings.AddRange(UnsureLinkFindings(db, wall.Id, inputs.Confirmation));
            if (findings.Count == 0)
            {
                return;
            }

            foreach (var finding in findings)
            {
                (finding.WallId, finding.SessionId, finding.Generation) = (wall.Id, session?.Id, newGen);
            }

            // Nothing below can throw: the rows and the flag are saved by the promote, together or not at all.
            db.WallLookOverFindings.AddRange(findings);
            wall.LookOverPendingSince ??= DateTimeOffset.UtcNow;
            wall.LookOverGeneration = newGen;
            logger.LogInformation("The update of wall {WallId} to generation {Generation} left {Count} findings for the look-over", wall.Id, newGen, findings.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "The look-over findings of the update of wall {WallId} could not be worked out; the update is applied without them.", wall.Id);
        }
    }

    /// <summary>
    /// A panel that was photographed again has findings of its own now; what was open on its old photo is history, and
    /// so is the hold it was about.
    /// </summary>
    private async Task SupersedeLookOverAsync(BlocwerkDbContext db, Guid wallId, IReadOnlyCollection<Guid> replacedPanelIds)
    {
        try
        {
            const LookOverFindingKind Discarded = LookOverFindingKind.DiscardedDetection;
            var stale = await db.WallLookOverFindings
                .Where(f => f.WallId == wallId && (f.Status == LookOverFindingStatus.Open || f.Kind == Discarded) && f.PanelId != null && replacedPanelIds.Contains(f.PanelId.Value))
                .ToListAsync();
            stale.Where(f => f.Kind != Discarded).ToList().ForEach(f => f.Status = LookOverFindingStatus.Superseded);

            // The discarded detections of the old photo cannot be added any more: they go with it, answered or not.
            db.WallLookOverFindings.RemoveRange(stale.Where(f => f.Kind == Discarded));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The page still refuses to change a hold that is no longer live, so a finding left open here is harmless.
            logger.LogError(ex, "The open look-over findings of wall {WallId} could not be retired; the update is applied all the same.", wallId);
        }
    }

    /// <summary>
    /// The detections a person answered "leave out" in the one review of this update (<see cref="WallUpdateReviewAnswer"/>):
    /// their findings are kept settled, not open. Read before the promote saves (the answers cascade with the detections).
    /// </summary>
    private async Task<IReadOnlyDictionary<Guid, WallUpdateReviewAnswer>> LeftOutByAPersonAsync(BlocwerkDbContext db, Guid wallId)
    {
        try
        {
            var session = await WallUpdateSessions.FindOpenAsync(db, wallId);
            if (session is null)
            {
                return new Dictionary<Guid, WallUpdateReviewAnswer>();
            }

            var rows = await db.WallUpdateReviewAnswers.AsNoTracking()
                .Where(a => a.SessionId == session.Id && (a.Answer == ReviewAnswer.LeaveOut || a.Answer == ReviewAnswer.NotAHold))
                .ToListAsync();
            return rows.GroupBy(a => a.HoldId).ToDictionary(g => g.Key, g => g.First());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "The review answers of the update of wall {WallId} could not be read; its discarded detections stay open.", wallId);
            return new Dictionary<Guid, WallUpdateReviewAnswer>();
        }
    }

    /// <summary>
    /// Keeps the staged detections this promote deletes because the triage discarded them (their rows carry the
    /// reason), as findings of their own kind: read from the change tracker before the promote saves, saved with it.
    /// One a person already answered "leave out" in the review is kept settled with that answer. They never flag the
    /// wall here; see <see cref="FlagForDiscardedDetectionsAsync"/>.
    /// </summary>
    private void KeepDiscardedDetections(BlocwerkDbContext db, Wall wall, int newGen, IReadOnlyDictionary<Guid, WallUpdateReviewAnswer> answered)
    {
        try
        {
            var kept = db.ChangeTracker.Entries<Hold>()
                .Where(e => e.State == EntityState.Deleted && DiscardedDetections.IsOne(e.Entity, wall.Id, newGen))
                .Select(e => SettledIfAnswered(DiscardedDetections.From(e.Entity), answered.GetValueOrDefault(e.Entity.Id)))
                .ToList();
            db.WallLookOverFindings.AddRange(kept);
            logger.LogInformation("The update of wall {WallId} to generation {Generation} keeps {Count} discarded detections", wall.Id, newGen, kept.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "The discarded detections of the update of wall {WallId} could not be kept; the update is applied without them.", wall.Id);
        }
    }

    private static WallLookOverFinding SettledIfAnswered(WallLookOverFinding finding, WallUpdateReviewAnswer? answer)
    {
        if (answer is not null)
        {
            (finding.Status, finding.Answer, finding.AnsweredByUserId, finding.AnsweredAt) =
                (LookOverFindingStatus.Settled, LookOverAnswer.Settle, answer.ByUserId, answer.At);
        }

        return finding;
    }

    /// <summary>
    /// After the promote is saved: works out which discarded detections a live hold covers, and flags the wall when
    /// one of them is likely a missed hold. Best effort, like the findings themselves.
    /// </summary>
    private async Task FlagForDiscardedDetectionsAsync(BlocwerkDbContext db, Wall wall, int newGen)
    {
        try
        {
            if (await DiscardedDetections.FlagIfLikelyMissedAsync(db, wall, newGen, CancellationToken.None))
            {
                logger.LogInformation("Wall {WallId} is flagged for a look-over: a discarded detection of generation {Generation} may be a missed hold", wall.Id, newGen);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "The discarded detections of wall {WallId} could not be compared with its holds.", wall.Id);
        }
    }

    /// <summary>
    /// Live hold to the boulders this promote marked only because holds of theirs were not found again (this hold, and
    /// maybe others: each has its own finding, and all of them have to be answered "still there" before the boulder is
    /// cleared). Read from the tracker, which still knows what a boulder was when it was loaded. Left out, so that
    /// nothing clears them later: a boulder that was marked before, one told anything else (a hold moved, was replaced
    /// or taken off), one that lost a membership (two of its holds became one), and one that uses a hold carried as
    /// "changed". With <paramref name="outcome"/> <see cref="HoldMoveOutcome.Replaced"/> it is the boulders marked only
    /// because this one hold was replaced by another (one reason, nothing else): the look-over's "use the new hold" may
    /// clear exactly those.
    /// </summary>
    private static Dictionary<Guid, List<Guid>> MarkedForOneHold(BlocwerkDbContext db, HoldMoveOutcome outcome = HoldMoveOutcome.NotFound)
    {
        var marked = db.ChangeTracker.Entries<Boulder>()
            .Where(e => e.Entity.NeedsReview && !e.Property(b => b.NeedsReview).OriginalValue)
            .Select(e => e.Entity.Id)
            .ToHashSet();
        var reasons = db.BoulderHoldMoves.Local.Where(m => db.Entry(m).State == EntityState.Added).ToList();
        var memberships = db.ChangeTracker.Entries<BoulderHold>().ToList();
        var changedHolds = db.HoldGenerationLinks.Local
            .Where(l => l.Kind == HoldGenerationLinkKind.Changed && l.NewHoldId is not null && db.Entry(l).State == EntityState.Added)
            .Select(l => l.NewHoldId!.Value)
            .ToHashSet();
        bool OnlyThisReason(Guid boulderId)
        {
            var mine = memberships.Where(e => e.Entity.BoulderId == boulderId).ToList();
            return marked.Contains(boulderId)
                && reasons.Where(m => m.BoulderId == boulderId).All(m => m.Outcome == outcome)
                && (outcome == HoldMoveOutcome.NotFound || reasons.Count(m => m.BoulderId == boulderId) == 1)
                && mine.Count(e => e.State == EntityState.Added) == mine.Count(e => e.State == EntityState.Deleted)
                && !mine.Any(e => e.State == EntityState.Added && changedHolds.Contains(e.Entity.HoldId));
        }

        return reasons
            .Where(m => m.Outcome == outcome && m.NewHoldId is not null && OnlyThisReason(m.BoulderId))
            .GroupBy(m => m.NewHoldId!.Value)
            .ToDictionary(g => g.Key, g => g.Select(m => m.BoulderId).Distinct().ToList());
    }

    /// <summary>What the promote did, read from its change tracker: nothing of it is saved yet.</summary>
    private static LookOverPromoteFacts ReadPromoteFacts(
        BlocwerkDbContext db, Guid wallId, int newGen, IReadOnlyList<WallUpdateException> cards, HashSet<Guid> unaligned, LookOverPromoteInputs inputs)
    {
        var live = db.Holds.Local
            .Where(h => h.WallId == wallId && h.Generation == newGen && db.Entry(h).State != EntityState.Deleted)
            .GroupBy(h => h.Id)
            .ToDictionary(g => g.Key, g => g.First());
        var successors = db.HoldGenerationLinks.Local
            .Where(l => l is { OldHoldId: not null, NewHoldId: not null } && l.ToGeneration == newGen && db.Entry(l).State == EntityState.Added)
            .GroupBy(l => l.OldHoldId!.Value)
            .ToDictionary(g => g.Key, g => g.First().NewHoldId!.Value);
        var boulders = db.BoulderHolds.Local
            .Where(bh => db.Entry(bh).State == EntityState.Added)
            .GroupBy(bh => bh.HoldId)
            .ToDictionary(g => g.Key, g => g.Select(bh => bh.BoulderId).Distinct().Count());
        var carry = inputs.Confirmation.Carryover.GroupBy(d => d.OldHoldId).ToDictionary(g => g.Key, g => g.First());
        var newlyMarked = MarkedForOneHold(db);
        return new LookOverPromoteFacts(
            cards,
            carry,
            successors,
            live,
            inputs.StagedHolds,
            inputs.Moves.NotFound ?? new Dictionary<Guid, HoldMoveOutcome>(),
            boulders,
            inputs.PhotoSizes,
            inputs.CardMarked,
            unaligned,
            newlyMarked,
            MarkedForOneHold(db, HoldMoveOutcome.Replaced));
    }
}
