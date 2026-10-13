// <copyright file="KeptHoldBackfill.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.LookOver;

/// <summary>
/// The one-time startup step for a wall whose current generation was applied by a version that already kept
/// look-over findings but did not yet mark the holds it could not find (<see cref="Hold.KeptUnconfirmedGeneration"/>):
/// it works out which live holds are copies the update carried forward without finding them, marks those nobody has
/// confirmed since, and gives each one a finding. Only walls whose look-over belongs to their current generation are
/// touched (an update applied before the look-over existed is left alone: nobody was told about its copies), and
/// <see cref="Wall.KeptHoldsGeneration"/> makes it run once, so a hold a person confirmed is never marked again.
/// </summary>
/// <remarks>
/// A copy is told from a hold that was found again by when its row was written: a detection was staged before the
/// update was applied, a copy was written by the apply itself, together with the lineage links. The change journal
/// has those times; without it the update session's carry decisions say which holds were paired with a detection.
/// </remarks>
public static class KeptHoldBackfill
{
    /// <summary>The journal batch label of a backfill.</summary>
    public const string BatchLabel = "kept-hold-backfill";

    /// <summary>Backfills every wall that needs it; a wall that fails is retried on the next start.</summary>
    /// <param name="factory">Context factory (the journalling one in production).</param>
    /// <param name="journal">Opens the per-wall batch.</param>
    /// <param name="logger">Logger.</param>
    /// <returns>A task.</returns>
    public static async Task RunIfNeededAsync(IDbContextFactory<BlocwerkDbContext> factory, IChangeJournal? journal, ILogger logger)
    {
        await using var list = await factory.CreateDbContextAsync();
        var wallIds = await list.Walls.IgnoreQueryFilters().AsNoTracking()
            .Where(w => w.LookOverGeneration == w.CurrentGeneration && (w.KeptHoldsGeneration == null || w.KeptHoldsGeneration != w.CurrentGeneration))
            .Select(w => w.Id)
            .ToListAsync();
        foreach (var wallId in wallIds)
        {
            try
            {
                await using var db = await factory.CreateDbContextAsync();
                using var batch = journal?.BeginBatch(BatchLabel, ChangeJournalScopeKind.Wall, wallId);
                var result = await BackfillWallAsync(db, wallId, CancellationToken.None);
                logger.LogInformation(
                    "Kept-hold backfill on wall {WallId}: {Kept} holds marked as not found again, {Findings} look-over findings added", wallId, result.Kept, result.Findings);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Kept-hold backfill failed for wall {WallId}; it is retried on the next start.", wallId);
            }
        }
    }

    /// <summary>Marks the wall's unconfirmed copies of its current generation, adds their missing findings, and saves.</summary>
    /// <param name="db">The context.</param>
    /// <param name="wallId">The wall.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>How many holds were marked and how many findings were added.</returns>
    internal static async Task<(int Kept, int Findings)> BackfillWallAsync(BlocwerkDbContext db, Guid wallId, CancellationToken ct)
    {
        var wall = await db.Walls.IgnoreQueryFilters().FirstAsync(w => w.Id == wallId, ct);
        var generation = wall.CurrentGeneration;
        var links = await db.HoldGenerationLinks.AsNoTracking()
            .Where(l => l.WallId == wallId && l.ToGeneration == generation && l.OldHoldId != null && l.NewHoldId != null)
            .Select(l => new CarriedLink(l.OldHoldId!.Value, l.NewHoldId!.Value, l.CreatedAt))
            .ToListAsync(ct);
        var live = await db.Holds.Where(h => h.WallId == wallId && h.Generation == generation).ToDictionaryAsync(h => h.Id, ct);
        var session = (await db.WallUpdateSessions.AsNoTracking()
                .Where(s => s.WallId == wallId && s.StagedGeneration == generation && s.Status == WallUpdateSessionStatus.Promoted)
                .ToListAsync(ct))
            .OrderByDescending(s => s.ClosedAt)
            .FirstOrDefault();
        var findings = await db.WallLookOverFindings
            .Where(f => f.WallId == wallId && f.Generation == generation && f.Kind != LookOverFindingKind.DiscardedDetection)
            .ToListAsync(ct);
        var copies = await CopiesAsync(db, wallId, session, links, live, ct);
        var confirmed = await ConfirmedAsync(db, session, findings, ct);
        var kept = links
            .Where(l => copies.Contains(l.NewHoldId) && live.TryGetValue(l.NewHoldId, out var hold) && !hold.IsVirtual
                && !confirmed.Contains(l.OldHoldId) && !confirmed.Contains(l.NewHoldId))
            .ToList();
        kept.ForEach(l => live[l.NewHoldId].KeptUnconfirmedGeneration = generation);
        var added = await AddFindingsAsync(db, wall, session?.Id, links, live, copies, findings, ct);
        wall.KeptHoldsGeneration = generation;
        if (added > 0)
        {
            wall.LookOverPendingSince ??= DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(ct);
        return (kept.Count, added);
    }

    /// <summary>
    /// The successors that are copies. By the journal when it recorded the update: a copy's row was inserted by the
    /// apply (not before the first lineage link was made), a detection's row when the photos were staged. Otherwise
    /// by the session's carry decisions: a successor no old hold was paired with is a copy.
    /// </summary>
    private static async Task<HashSet<Guid>> CopiesAsync(
        BlocwerkDbContext db, Guid wallId, WallUpdateSession? session, List<CarriedLink> links, Dictionary<Guid, Hold> live, CancellationToken ct)
    {
        var successors = links.Select(l => l.NewHoldId).Where(live.ContainsKey).ToHashSet();
        if (successors.Count == 0)
        {
            return [];
        }

        var applied = links.Min(l => l.CreatedAt);
        var inserts = await db.ChangeJournalEntries.AsNoTracking()
            .Where(e => e.EntityType == nameof(Hold) && e.Op == ChangeJournalOp.Insert
                && db.ChangeJournalBatches.Any(b => b.Id == e.BatchId && b.ScopeId == wallId))
            .Select(e => new { e.KeyJson, e.CreatedAt })
            .ToListAsync(ct);
        var inserted = inserts
            .Select(e => (Id: HoldId(e.KeyJson), e.CreatedAt))
            .Where(e => e.Id is { } id && successors.Contains(id))
            .GroupBy(e => e.Id!.Value)
            .ToDictionary(g => g.Key, g => g.Max(e => e.CreatedAt));
        if (inserted.Count > 0)
        {
            return inserted.Where(e => e.Value >= applied).Select(e => e.Key).ToHashSet();
        }

        if (session is null)
        {
            return [];
        }

        var paired = await db.WallUpdateHoldDecisions.AsNoTracking()
            .Where(d => d.SessionId == session.Id && d.Kind == WallUpdateHoldDecisionKind.Carry && d.PairedHoldId != null)
            .Select(d => d.PairedHoldId!.Value)
            .ToListAsync(ct);
        return successors.Except(paired).ToHashSet();
    }

    /// <summary>Holds a person already decided: confirmed as kept in the review (old hold ids), or answered "it is fine" in the look-over (live hold ids).</summary>
    private static async Task<HashSet<Guid>> ConfirmedAsync(BlocwerkDbContext db, WallUpdateSession? session, List<WallLookOverFinding> findings, CancellationToken ct)
    {
        var confirmed = findings
            .Where(f => f is { Status: LookOverFindingStatus.Settled, Answer: LookOverAnswer.Settle, HoldId: not null } && LookOverRules.CanHaveOther(f.Kind))
            .Select(f => f.HoldId!.Value)
            .ToHashSet();
        if (session is not null)
        {
            confirmed.UnionWith(await db.WallUpdateHoldDecisions.AsNoTracking()
                .Where(d => d.SessionId == session.Id && d.Kind == WallUpdateHoldDecisionKind.Carry && d.Confirmed)
                .Select(d => d.HoldId)
                .ToListAsync(ct));
        }

        return confirmed;
    }

    /// <summary>
    /// Runs the promote's own rule (<see cref="LookOverFindingBuilder"/>) over the wall as it is, without the cards the
    /// promote already turned into findings: every marked copy without a finding gets one, and the open findings about
    /// a marked copy learn that it is one and whether it lies under a new hold.
    /// </summary>
    private static async Task<int> AddFindingsAsync(
        BlocwerkDbContext db, Wall wall, Guid? sessionId, List<CarriedLink> links, Dictionary<Guid, Hold> live, HashSet<Guid> copies,
        List<WallLookOverFinding> findings, CancellationToken ct)
    {
        var sizes = await LookOverFrameSource.SizesAsync(db, live.Values.Select(h => h.WallPanelId).OfType<Guid>().Distinct().ToList(), ct);
        var boulders = (await db.BoulderHolds.AsNoTracking()
                .Where(bh => bh.Hold.WallId == wall.Id && !bh.Boulder.IsArchived && !bh.Boulder.IsHistoric)
                .Select(bh => new { bh.HoldId, bh.BoulderId })
                .ToListAsync(ct))
            .GroupBy(b => b.HoldId)
            .ToDictionary(g => g.Key, g => g.Select(b => b.BoulderId).Distinct().Count());
        var facts = new LookOverPromoteFacts(
            [],
            new Dictionary<Guid, CarryoverDecision>(),
            links.Where(l => live.ContainsKey(l.NewHoldId)).GroupBy(l => l.OldHoldId).ToDictionary(g => g.Key, g => g.First().NewHoldId),
            live,
            live.Where(h => !copies.Contains(h.Key)).ToDictionary(h => h.Key, h => h.Value),
            new Dictionary<Guid, HoldMoveOutcome>(),
            boulders,
            sizes,
            new HashSet<Guid>(),
            new HashSet<Guid>(),
            new Dictionary<Guid, List<Guid>>());
        var known = findings.Where(f => f.OldHoldId is not null).GroupBy(f => f.OldHoldId!.Value).ToDictionary(g => g.Key, g => g.ToList());
        var added = 0;
        foreach (var built in LookOverFindingBuilder.Build(facts).Where(f => f is { HoldKept: true, OldHoldId: not null }))
        {
            if (known.TryGetValue(built.OldHoldId!.Value, out var existing))
            {
                foreach (var open in existing.Where(f => f.Status == LookOverFindingStatus.Open && f.HoldId == built.HoldId))
                {
                    open.HoldKept = true;
                    open.OtherHoldId ??= built.OtherHoldId;
                    open.Covered = open.OtherHoldId is { } other && live.TryGetValue(other, out var covering)
                        && SameSpotFinder.Covers(covering, live[built.HoldId!.Value], Size(sizes, covering));
                }

                continue;
            }

            // Which boulders the update marked for this hold is not known any more: none, so nothing is cleared for it.
            (built.WallId, built.SessionId, built.Generation, built.MarkedBoulderIds) = (wall.Id, sessionId, wall.CurrentGeneration, []);
            db.WallLookOverFindings.Add(built);
            added++;
        }

        return added;
    }

    private static (int Width, int Height)? Size(IReadOnlyDictionary<Guid, (int Width, int Height)> sizes, Hold hold) =>
        hold.WallPanelId is { } panel && sizes.TryGetValue(panel, out var size) ? size : null;

    private static Guid? HoldId(string keyJson)
    {
        try
        {
            using var key = JsonDocument.Parse(keyJson);
            return key.RootElement.TryGetProperty("Id", out var id) && id.TryGetGuid(out var guid) ? guid : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
