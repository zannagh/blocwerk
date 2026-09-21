using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

/// <summary>
/// Per-panel hold re-detection. It operates on a single panel's own image and holds in that panel's
/// coordinate space, so it never touches a neighbour panel. Only a panel's auto-detected holds that
/// no boulder depends on may be removed, so a redetect can never orphan a boulder. Mutations are
/// gated by <see cref="WallAdminGuard"/> (editor).
/// </summary>
/// <remarks>
/// A "clean panel artifacts" tool used to live here: one toolbar button that deleted every
/// unreferenced auto-detected hold on the panel at the current generation, with no confidence
/// threshold, no size filter, no preview and no confirmation. On 2026-09-20 an admin hit it by
/// accident on a live wall and it destroyed 181 real holds (restored from the change journal). It
/// was removed end to end. Any future "clean up detections" feature must show what it would delete
/// and take an explicit confirmation — never a single unguarded toolbar button.
/// </remarks>
public partial class WallPanelService
{
    /// <summary>
    /// Re-runs auto hold detection on a live panel's own image. The panel's auto-detected holds at the
    /// current generation that no boulder uses are replaced with the fresh detections; manual holds and
    /// any boulder-referenced hold are kept untouched. A panel with no live photo is a no-op. Returns
    /// the number of freshly detected holds.
    /// </summary>
    public async Task<int> RedetectPanelHoldsAsync(Guid wallId, Guid panelId)
    {
        var user = await currentUserService.GetCurrentUserAsync();
        await using var db = await dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = user.Id;
        await WallAdminGuard.EnsureWallEditorAsync(db, wallId, user.Id, CancellationToken.None);

        var wall = await db.Walls.FirstOrDefaultAsync(w => w.Id == wallId);
        if (wall is null)
        {
            throw new InvalidOperationException("Wall not found");
        }

        var panel = await db.WallPanels.FirstOrDefaultAsync(p => p.Id == panelId && p.WallId == wallId);
        if (panel is null)
        {
            throw new InvalidOperationException("Panel not found");
        }

        if (panel.Photo is null)
        {
            return 0;
        }

        var removable = await CollectRemovableAutoHoldsAsync(db, wallId, panelId, wall.CurrentGeneration);
        await PrepareRemovableAsync(db, removable);
        db.Holds.RemoveRange(removable);

        var detected = await holdDetectionService.DetectHoldsAsync(panel.Photo);
        foreach (var d in detected)
        {
            db.Holds.Add(new Hold
            {
                WallId = wallId,
                WallPanelId = panelId,
                X = d.X,
                Y = d.Y,
                Radius = d.Radius,
                Color = d.Color,
                Confidence = d.Confidence,
                IsAutoDetected = true,
                NeedsReview = true,
                Generation = wall.CurrentGeneration,
            });
        }

        // The panel's holds were just replaced, so the links pointing at the old ones stopped meaning
        // anything: the editor has to look at cross-panel linking again.
        wall.LinksFinalizedGeneration = null;

        await db.SaveChangesAsync();
        logger.LogInformation(
            "Panel {PanelId} holds redetected on wall {WallId} by {UserId}: removed {RemovedCount}, detected {DetectedCount}",
            panelId, wallId, user.Id, removable.Count, detected.Count);
        return detected.Count;
    }

    /// <summary>
    /// The panel's auto-detected holds at <paramref name="generation"/> that no boulder references —
    /// the only holds a redetect may delete. Boulder-referenced and manual holds are excluded so a
    /// redetect can never orphan a boulder (the Hold→BoulderHold FK is Restrict regardless).
    /// </summary>
    private static async Task<List<Hold>> CollectRemovableAutoHoldsAsync(
        BlocwerkDbContext db, Guid wallId, Guid panelId, int generation)
    {
        var autoHolds = await db.Holds
            .Where(h => h.WallPanelId == panelId
                        && h.WallId == wallId
                        && h.IsAutoDetected
                        && h.Generation == generation)
            .ToListAsync();
        if (autoHolds.Count == 0)
        {
            return autoHolds;
        }

        var autoIds = autoHolds.Select(h => h.Id).ToList();
        var referenced = (await db.BoulderHolds
                .Where(bh => autoIds.Contains(bh.HoldId))
                .Select(bh => bh.HoldId)
                .Distinct()
                .ToListAsync())
            .ToHashSet();

        return autoHolds.Where(h => !referenced.Contains(h.Id)).ToList();
    }

    /// <summary>
    /// Clears what the Restrict FKs would otherwise block on. The boulder guard above already keeps
    /// referenced holds out of <paramref name="removable"/>, so memberships are left untouched; panel
    /// links and cross-generation lineage are NOT covered by that guard and must be handled — an
    /// auto-detected hold that a promote later carried forward does own lineage rows.
    /// </summary>
    private static Task PrepareRemovableAsync(BlocwerkDbContext db, List<Hold> removable)
    {
        return HoldDeletion.PrepareHoldsForDeleteAsync(
            db, removable.Select(h => h.Id).ToList(), HoldDeleteBoulderPolicy.LeaveUntouched);
    }
}
