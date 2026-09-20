using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Data;

/// <summary>
/// Converges every wall onto the big-wall model: a wall with a live <see cref="Wall.Photo"/> gets a
/// center <see cref="WallPanel"/> at (0,0) mirroring that photo, its current-generation unassigned
/// holds are re-parented onto it, and <see cref="Wall.UsesMultipleImages"/> is set true for every
/// wall that has any panel. This is the idempotent, one-way replacement for the removed
/// <c>EnableMultiImageAsync</c> toggle. Also reconciles the asymmetric states the old toggle left
/// behind (panels present but the flag off; the flag on but no center panel yet).
/// </summary>
/// <remarks>
/// Runs on every start like the other backfills and no-ops once converged (it only writes when a
/// wall actually changes). An in-flight single-image staged edit (<see cref="Wall.StagedPhoto"/> /
/// <see cref="Wall.StagingMode"/>) is DISCARDED here — it was an unconfirmed in-progress update and
/// the single-image staging entry points that would have committed it are gone. Committed holds and
/// boulders (the live generation) are never touched; a staged hold a boulder already links is rescued
/// down into the live generation rather than deleted, mirroring the old discard path. Walls with no
/// photo and no panels are left as a 0-panel "upload-prompt" wall.
/// </remarks>
public static class WallCenterPanelConvergence
{
    public static async Task RunIfNeededAsync(IDbContextFactory<BlocwerkDbContext> factory, ILogger logger)
    {
        await using var db = await factory.CreateDbContextAsync();

        // System-level backfill: read every wall regardless of the membership/kiosk query filter.
        var wallIds = await db.Walls.IgnoreQueryFilters().Select(w => w.Id).ToListAsync();
        if (wallIds.Count == 0)
        {
            return;
        }

        var convergedWalls = 0;
        foreach (var wallId in wallIds)
        {
            // Isolate each wall: one wall's SaveChanges failure must not abort the whole converge.
            try
            {
                if (await ConvergeWallAsync(db, wallId))
                {
                    convergedWalls++;
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Wall center-panel converge failed for wall {WallId}; skipping it.", wallId);
            }
        }

        logger.LogInformation("Wall center-panel converge: converged {ConvergedCount} wall(s).", convergedWalls);
    }

    private static async Task<bool> ConvergeWallAsync(BlocwerkDbContext db, Guid wallId)
    {
        var wall = await db.Walls.IgnoreQueryFilters().FirstAsync(w => w.Id == wallId);
        var panels = await db.WallPanels.Where(p => p.WallId == wallId).ToListAsync();

        var stagedGeneration = wall.CurrentGeneration + 1;

        // Single-image staged holds are ALWAYS null-panel. Big-wall staged holds sit at the same
        // generation but are panel-parented — they belong to a legitimate in-flight panel update and
        // must never be discarded here, so scope the discard to null-panel holds only.
        var stagedHolds = await db.Holds
            .Include(h => h.BoulderHolds)
            .Where(h => h.WallId == wallId
                && h.Generation == stagedGeneration
                && h.WallPanelId == null)
            .ToListAsync();
        var unassignedLiveHolds = await db.Holds
            .Where(h => h.WallId == wallId
                && h.Generation == wall.CurrentGeneration
                && h.WallPanelId == null)
            .ToListAsync();

        var changed = await DiscardSingleImageStagingAsync(db, wall, stagedHolds);
        changed |= EnsureCenterPanel(db, wall, panels, unassignedLiveHolds);

        if (changed)
        {
            await db.SaveChangesAsync();
        }

        return changed;
    }

    /// <summary>
    /// Ensures a wall with a live photo carries a (0,0) center panel and that any wall with a panel is
    /// flagged multi-image. Pure mutation on tracked entities — the caller commits. Idempotent: a
    /// no-op once a center panel exists and the flag is set. Returns whether anything changed.
    /// </summary>
    internal static bool EnsureCenterPanel(
        BlocwerkDbContext db,
        Wall wall,
        IReadOnlyCollection<WallPanel> panels,
        IReadOnlyCollection<Hold> currentGenerationUnassignedHolds)
    {
        // A LIVE center panel means a (0,0) panel that actually carries bytes — the photo endpoint only
        // serves a (0,0) panel with Photo != null, so a staged-only (Photo == null) center must not
        // block seeding a real one.
        var hasCenter = panels.Any(p => p.Col == 0 && p.Row == 0 && p.Photo != null);
        var changed = false;

        if (!hasCenter && wall.Photo is not null)
        {
            var center = new WallPanel
            {
                WallId = wall.Id,
                Col = 0,
                Row = 0,
                Photo = (byte[])wall.Photo.Clone(),
                PhotoContentType = wall.PhotoContentType,
                Generation = wall.CurrentGeneration,
            };
            db.WallPanels.Add(center);

            foreach (var hold in currentGenerationUnassignedHolds)
            {
                if (hold.Generation == wall.CurrentGeneration && hold.WallPanelId is null)
                {
                    hold.WallPanelId = center.Id;
                }
            }

            hasCenter = true;
            changed = true;
        }

        // Every wall that now has a panel is a big wall; walls with neither photo nor panels stay as a
        // 0-panel upload-prompt wall and keep whatever the flag was.
        if ((hasCenter || panels.Count > 0) && !wall.UsesMultipleImages)
        {
            wall.UsesMultipleImages = true;
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// Discards an in-flight single-image staged edit: clears the staged photo fields and staging mode
    /// and drops the staged-generation holds, rescuing any a boulder already links down into the live
    /// generation (the restricted BoulderHold FK would otherwise block the delete). No save; returns
    /// whether anything changed.
    /// </summary>
    internal static async Task<bool> DiscardSingleImageStagingAsync(
        BlocwerkDbContext db,
        Wall wall,
        IReadOnlyCollection<Hold> stagedGenerationHolds)
    {
        if (wall.StagedPhoto is null && wall.StagingMode == WallStagingMode.None)
        {
            return false;
        }

        var removable = new List<Guid>();
        foreach (var hold in stagedGenerationHolds)
        {
            if (hold.BoulderHolds.Count > 0)
            {
                hold.Generation = wall.CurrentGeneration;
            }
            else
            {
                removable.Add(hold.Id);
                db.Holds.Remove(hold);
            }
        }

        // Boulder-linked staged holds were rescued above rather than deleted, so no membership can be
        // in the way here; panel links and cross-generation lineage still have to be cleared, or this
        // startup backfill would throw on a wall that ever ran a generation update.
        await HoldDeletion.PrepareHoldsForDeleteAsync(db, removable, HoldDeleteBoulderPolicy.LeaveUntouched);

        wall.StagedPhoto = null;
        wall.StagedPhotoContentType = null;
        wall.StagedAt = null;
        wall.StagedByUserId = null;
        wall.StagingMode = WallStagingMode.None;
        return true;
    }
}
