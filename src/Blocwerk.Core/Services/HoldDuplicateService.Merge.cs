using Blocwerk.Core.Data;
using Blocwerk.Core.Detection.Outlines;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

/// <summary>
/// The merge. The removed hold's boulders move onto the kept hold (a boulder that used both keeps the more prominent role,
/// top over start over a plain hold, and the combined use), the useful flags are carried over, and the removed hold goes
/// through the normal hold-deletion preparation. Everything is one save inside one journal batch, so it reverts exactly.
/// </summary>
/// <remarks>
/// A boulder is flagged for review only when it used BOTH holds: its hold set shrank by one and the surviving mark may be a
/// different role than one of the two it had, which a climber would notice. A boulder that used just one of them still has
/// the same physical hold, now under one record, so nothing a climber sees changes and it is left alone. Archived and
/// historic boulders are moved but never flagged (they are frozen records).
/// </remarks>
public sealed partial class HoldDuplicateService
{
    /// <inheritdoc/>
    public async Task<HoldMergeResult> MergeAsync(Guid wallId, Guid leftId, Guid rightId, HoldMergeMode mode, CancellationToken ct = default)
    {
        var (db, userId) = await OpenForAdminAsync(wallId, ct);
        await using (db)
        {
            using var wallLock = AcquireWallLock(wallId);
            var left = await db.Holds.FirstOrDefaultAsync(h => h.Id == leftId && h.WallId == wallId, ct);
            var right = await db.Holds.FirstOrDefaultAsync(h => h.Id == rightId && h.WallId == wallId, ct);
            if (left is null || right is null || leftId == rightId)
            {
                throw new UserFacingException("One of these holds no longer exists. Reload the list.");
            }

            if (left.WallPanelId != right.WallPanelId || left.Generation != right.Generation)
            {
                throw new UserFacingException("These holds are on different photos, so they cannot be merged.");
            }

            var (kept, removed) = Choose(left, right, mode);
            var (moved, flagged) = await MoveBouldersAsync(db, kept, removed, ct);
            if (mode == HoldMergeMode.KeepHandUseDetectedShape)
            {
                HoldMergeRules.AdoptDetectedShape(kept, removed);
            }

            HoldMergeRules.MergeFlags(kept, removed);
            await HoldDeletion.PrepareHoldForDeleteAsync(db, removed.Id, HoldDeleteBoulderPolicy.LeaveUntouched, ct: ct);
            db.Holds.Remove(removed);
            ct.ThrowIfCancellationRequested();
            var batchId = await SaveInBatchAsync(db, wallId, kept.Id, ct);
            logger.LogInformation(
                "Merged hold {Removed} into {Kept} on wall {WallId} by {UserId} ({Mode}): {Moved} boulders moved, {Flagged} flagged, journal batch {BatchId}",
                removed.Id, kept.Id, wallId, userId, mode, moved, flagged, batchId);
            return new HoldMergeResult(batchId, kept.Id, removed.Id, moved, flagged);
        }
    }

    private static (Hold Kept, Hold Removed) Choose(Hold left, Hold right, HoldMergeMode mode)
    {
        switch (mode)
        {
            case HoldMergeMode.KeepLeft:
                return (left, right);
            case HoldMergeMode.KeepRight:
                return (right, left);
            default:
                if (HoldMergeRules.IsHandMade(left) == HoldMergeRules.IsHandMade(right))
                {
                    throw new UserFacingException("Only a hand-placed hold next to an automatic detection can take the detected shape. Choose which hold to keep instead.");
                }

                return HoldMergeRules.IsHandMade(left) ? (left, right) : (right, left);
        }
    }

    private static async Task<(int Moved, int Flagged)> MoveBouldersAsync(BlocwerkDbContext db, Hold kept, Hold removed, CancellationToken ct)
    {
        var links = await db.BoulderHolds.Include(bh => bh.Boulder)
            .Where(bh => bh.HoldId == kept.Id || bh.HoldId == removed.Id)
            .ToListAsync(ct);
        var keptLinks = links.Where(l => l.HoldId == kept.Id).ToDictionary(l => l.BoulderId);
        int moved = 0;
        int flagged = 0;
        foreach (var link in links.Where(l => l.HoldId == removed.Id))
        {
            db.BoulderHolds.Remove(link);
            if (keptLinks.TryGetValue(link.BoulderId, out var existing))
            {
                existing.Type = HoldMergeRules.MergeType(existing.Type, link.Type);
                existing.Usage = HoldMergeRules.MergeUsage(existing.Usage, link.Usage);
                if (link.Boulder is { IsArchived: false, IsHistoric: false })
                {
                    link.Boulder.NeedsReview = true;
                    flagged++;
                }

                continue;
            }

            // The hold id is part of the key, so a move is a delete plus an insert.
            db.BoulderHolds.Add(new BoulderHold { BoulderId = link.BoulderId, HoldId = kept.Id, Type = link.Type, Usage = link.Usage });
            moved++;
        }

        return (moved, flagged);
    }

    /// <summary>Saves inside a named batch and returns the id of exactly the batch that was opened for this write.</summary>
    private async Task<Guid> SaveInBatchAsync(BlocwerkDbContext db, Guid wallId, Guid keptId, CancellationToken ct)
    {
        using var scope = journal.BeginBatch(BatchLabel, ChangeJournalScopeKind.Wall, wallId);
        var batchId = ((ChangeJournalBatchScope)scope).BatchId;
        await db.SaveChangesAsync(ct);

        // What the merge carried onto the kept hold (e.g. the kickboard flag) now reaches its linked twins, in the
        // same batch so the undo reverts both. The removed hold's links are gone by now.
        var sync = await LinkedHoldSync.ReconcileAsync(db, wallId, keptId, ct);
        if (sync.Any)
        {
            await db.SaveChangesAsync(ct);
        }

        return batchId;
    }
}
