// <copyright file="HoldMergeSplitService.Merge.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Blocwerk.Core.Detection.Outlines;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.HoldStacks;
using Blocwerk.Core.Telemetry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

/// <summary>
/// The merge tool. Unlike the duplicate review (<see cref="HoldDuplicateService"/>), which deals with two records of ONE
/// hold and drops the loser's history, this joins two records that both lived on: the kept hold keeps its id, lineage
/// and panel links, and ADOPTS the removed hold's lineage and links (<see cref="HoldRelinking"/>) instead of tombstoning
/// them. Boulders move with the duplicate review's rule (<see cref="HoldDuplicateService.MoveBouldersAsync"/>: the more
/// prominent role wins, a boulder that used both is flagged for review), the stacks are re-based
/// (<see cref="HoldStacker.MergeAsync"/>) and the outline becomes the union of both (bridged across a gap).
/// </summary>
public sealed partial class HoldMergeSplitService
{
    /// <inheritdoc/>
    public async Task<HoldMergePreview> PreviewMergeAsync(Guid wallId, Guid firstId, Guid secondId, CancellationToken ct = default)
    {
        var (db, _) = await OpenAsync(wallId, "Merging holds", ct);
        await using (db)
        {
            var (first, second) = await LoadPairAsync(db, wallId, firstId, secondId, ct);
            var merged = await MergedOutlineAsync(db, first, second, ct);
            var counts = await ActiveBouldersAsync(db, first.Id, second.Id, ct);
            var kept = await SuggestKeptAsync(db, first, second, counts, ct);
            var both = counts.Where(c => c.Value.Count == 2).Count();
            var perHold = new Dictionary<Guid, int>
            {
                [first.Id] = counts.Count(c => c.Value.Contains(first.Id)),
                [second.Id] = counts.Count(c => c.Value.Contains(second.Id)),
            };
            return new HoldMergePreview(kept.Id, ToOutline(merged.Shape), merged.Bridged, perHold, both);
        }
    }

    /// <inheritdoc/>
    public async Task<HoldToolResult> MergeAsync(Guid wallId, Guid keptId, Guid removedId, CancellationToken ct = default)
    {
        var (db, userId) = await OpenAsync(wallId, "Merging holds", ct);
        await using (db)
        {
            using var wallLock = WallHoldWriteLock.TryAcquire(wallId, BusyMessage);
            var (kept, removed) = await LoadPairAsync(db, wallId, keptId, removedId, ct);
            var merged = await MergedOutlineAsync(db, kept, removed, ct);

            var (moved, flagged) = await HoldDuplicateService.MoveBouldersAsync(db, kept, removed, null, ct);
            HoldMergeRules.MergeFlags(kept, removed);
            merged.Shape.WriteTo(kept);
            await HoldStacker.MergeAsync(db, kept, removed, ct);
            await HoldRelinking.RepointHoldLinksAsync(db, kept.Id, removed.Id, ct);
            await HoldRelinking.RepointGenerationLinksAsync(db, kept.Id, removed.Id, ct);

            // Everything that pointed at the removed hold points at the kept one now; what is left (nothing, normally)
            // goes through the one deletion path like every other hold delete.
            await HoldDeletion.PrepareHoldForDeleteAsync(db, removed.Id, HoldDeleteBoulderPolicy.LeaveUntouched, ct: ct);
            db.Holds.Remove(removed);
            ct.ThrowIfCancellationRequested();
            var batchId = await SaveInBatchAsync(db, MergeBatchLabel, wallId, kept.Id, ct);

            QueueFootprints(wallId, kept.Id);
            BlocwerkMetrics.RecordHoldUpdated(wallId, "merged");
            logger.LogInformation(
                "Merge tool: hold {Removed} merged into {Kept} on wall {WallId} by {UserId} (bridged {Bridged}): {Moved} boulders moved, {Flagged} flagged, batch {BatchId}",
                removed.Id, kept.Id, wallId, userId, merged.Bridged, moved, flagged, batchId);
            return new HoldToolResult(batchId, kept.Id, removed.Id, moved, flagged);
        }
    }

    private static async Task<(Hold First, Hold Second)> LoadPairAsync(BlocwerkDbContext db, Guid wallId, Guid firstId, Guid secondId, CancellationToken ct)
    {
        if (firstId == secondId)
        {
            throw new UserFacingException("Pick two different holds to merge.");
        }

        var first = await LoadLiveAsync(db, wallId, firstId, ct);
        var second = await LoadLiveAsync(db, wallId, secondId, ct);
        if (first.WallPanelId != second.WallPanelId || first.Generation != second.Generation)
        {
            throw new UserFacingException("These holds are on different photos, so they cannot be merged.");
        }

        return (first, second);
    }

    private static async Task<HoldOutlineMerge> MergedOutlineAsync(BlocwerkDbContext db, Hold a, Hold b, CancellationToken ct)
    {
        double aspect = await AspectAsync(db, a, ct);
        var ra = OutlineRings.Of(a);
        var rb = OutlineRings.Of(b);

        // A gap up to one and a half times the larger hold's size is bridged; further apart they are not one hold.
        double size = Math.Sqrt(Math.Max(ra.Area, rb.Area) * aspect / Math.PI);
        double maxGap = Math.Max(0.01, 1.5 * size);
        if (HoldOutlineBoolean.Gap(ra, rb, aspect) > maxGap)
        {
            throw new UserFacingException("These holds are too far apart to be one hold.");
        }

        return HoldOutlineBoolean.Merge(ra, rb, aspect, maxGap)
            ?? throw new UserFacingException("The two outlines could not be joined into one shape. Reshape one of them so they touch, then merge.");
    }

    /// <summary>Per active boulder on either hold, which of the two it uses.</summary>
    private static async Task<Dictionary<Guid, HashSet<Guid>>> ActiveBouldersAsync(BlocwerkDbContext db, Guid a, Guid b, CancellationToken ct)
    {
        var rows = await db.BoulderHolds.AsNoTracking()
            .Where(bh => (bh.HoldId == a || bh.HoldId == b) && !bh.Boulder.IsArchived && !bh.Boulder.IsHistoric)
            .Select(bh => new { bh.BoulderId, bh.HoldId })
            .ToListAsync(ct);
        return rows.GroupBy(r => r.BoulderId).ToDictionary(g => g.Key, g => g.Select(r => r.HoldId).ToHashSet());
    }

    /// <summary>The hold that should stay: more boulders, then lineage from an earlier photo, then more panel links, then the larger.</summary>
    private static async Task<Hold> SuggestKeptAsync(BlocwerkDbContext db, Hold a, Hold b, Dictionary<Guid, HashSet<Guid>> boulders, CancellationToken ct)
    {
        async Task<(int, int, int, double)> RankAsync(Hold h)
        {
            var count = boulders.Count(x => x.Value.Contains(h.Id));
            var lineage = await db.HoldGenerationLinks.CountAsync(l => l.NewHoldId == h.Id && l.OldHoldId != null, ct);
            var links = await db.HoldLinks.CountAsync(l => l.HoldAId == h.Id || l.HoldBId == h.Id, ct);
            return (count, lineage, links, OutlineRings.Of(h).Area);
        }

        var ra = await RankAsync(a);
        var rb = await RankAsync(b);
        int order = ra.CompareTo(rb);
        return order > 0 || (order == 0 && a.Id.CompareTo(b.Id) <= 0) ? a : b;
    }
}
