using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

/// <summary>
/// The commit and abandon halves of the big-wall update, plus the pure helpers shared with the
/// session builder. Promotion preserves boulders by editing the SAME live <see cref="Hold"/> rows in
/// place (never re-pointing their <see cref="BoulderHold"/>s) and consuming the staged clones.
/// </summary>
public partial class WallBigUpdateService
{
    /// <inheritdoc/>
    public async Task PromoteAsync(Guid wallId, BigUpdateConfirmation confirmation)
    {
        var user = await currentUserService.GetCurrentUserAsync();
        await using var db = await dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = user.Id;
        await WallAdminGuard.EnsureWallAdminAsync(db, wallId, user.Id, CancellationToken.None);

        var wall = await db.Walls.FirstOrDefaultAsync(w => w.Id == wallId)
            ?? throw new InvalidOperationException("Wall not found");

        var oldGen = wall.CurrentGeneration;
        var stagedGen = oldGen + 1;
        var newGen = stagedGen;

        var centerPanel = await db.WallPanels.FirstOrDefaultAsync(p =>
            p.WallId == wallId && p.Col == 0 && p.Row == 0
            && p.Generation == stagedGen && p.StagedPhoto != null)
            ?? throw new InvalidOperationException("No in-flight big update to promote.");

        var oldHolds = await db.Holds
            .Where(h => h.WallId == wallId && h.Generation == oldGen)
            .ToDictionaryAsync(h => h.Id);
        var centerStaged = await db.Holds
            .Where(h => h.WallPanelId == centerPanel.Id && h.Generation == stagedGen)
            .ToDictionaryAsync(h => h.Id);

        // Archive the outgoing photo before the generation is bumped.
        if (wall.Photo is not null)
        {
            db.WallResets.Add(new WallReset
            {
                WallId = wall.Id,
                Generation = oldGen,
                PreviousPhoto = wall.Photo,
                PreviousPhotoContentType = wall.PhotoContentType,
                ResetByUserId = user.Id,
            });
        }

        // Maps a consumed-or-accepted staged centre hold to the live hold that carries its position,
        // so neighbour links that referenced a centre hold survive the carry step.
        var centerStagedToLive = new Dictionary<Guid, Guid>();
        var consumedStaged = new HashSet<Guid>();

        // CENTRE carryover: edit the old hold rows in place — identity preserved → boulders survive.
        foreach (var decision in confirmation.Carryover)
        {
            if (!oldHolds.TryGetValue(decision.OldHoldId, out var oldHold))
            {
                continue;
            }

            if (decision.Kind == CarryKind.Removed)
            {
                await RemoveHoldAsync(db, oldHold);
                continue;
            }

            if (decision.NewHoldId is not { } newHoldId
                || !centerStaged.TryGetValue(newHoldId, out var staged))
            {
                // A carry with no valid staged twin: leave the old hold in place at the new generation.
                oldHold.WallPanelId = centerPanel.Id;
                oldHold.Generation = newGen;
                oldHold.NeedsReview = decision.Kind == CarryKind.Moved;
                continue;
            }

            oldHold.X = staged.X;
            oldHold.Y = staged.Y;
            oldHold.Radius = staged.Radius;
            oldHold.WallPanelId = centerPanel.Id;
            oldHold.Generation = newGen;
            oldHold.NeedsReview = decision.Kind == CarryKind.Moved;

            centerStagedToLive[newHoldId] = oldHold.Id;
            consumedStaged.Add(newHoldId);
            db.Holds.Remove(staged);
        }

        // New-on-centre: accepted staged holds become live; every other staged centre hold is dropped.
        var accepted = confirmation.AcceptedNewCenterHoldIds.ToHashSet();
        foreach (var (stagedId, staged) in centerStaged)
        {
            if (consumedStaged.Contains(stagedId))
            {
                continue;
            }

            if (accepted.Contains(stagedId))
            {
                staged.Generation = newGen;
                centerStagedToLive[stagedId] = stagedId;
            }
            else
            {
                db.Holds.Remove(staged);
            }
        }

        // Centre panel goes live at (0,0).
        centerPanel.Photo = centerPanel.StagedPhoto;
        centerPanel.PhotoContentType = centerPanel.StagedPhotoContentType;
        ClearStaged(centerPanel);

        await PromoteNeighboursAsync(db, wallId, stagedGen, newGen, centerPanel.Id, centerStagedToLive, confirmation, user.Id);

        wall.Photo = centerPanel.Photo;
        wall.PhotoContentType = centerPanel.PhotoContentType;
        wall.UsesMultipleImages = true;
        wall.CurrentGeneration = newGen;
        wall.LastResetAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync();
        logger.LogInformation(
            "Big update promoted on wall {WallId} by {UserId}: generation {Old}->{New}", wallId, user.Id, oldGen, newGen);
    }

    /// <summary>
    /// Brings every non-centre staged panel live, promotes its staged holds to the new generation,
    /// applies the user's removals, and creates the confirmed hold links — remapping any link whose
    /// centre end was a staged hold that a carry consumed onto the live hold that absorbed it, and
    /// dropping links whose centre end was discarded. No SaveChanges: the caller commits atomically.
    /// </summary>
    private static async Task PromoteNeighboursAsync(
        BlocwerkDbContext db,
        Guid wallId,
        int stagedGen,
        int newGen,
        Guid centerPanelId,
        IReadOnlyDictionary<Guid, Guid> centerStagedToLive,
        BigUpdateConfirmation confirmation,
        Guid userId)
    {
        var panels = await db.WallPanels
            .Where(p => p.WallId == wallId && p.Generation == stagedGen
                && p.StagedPhoto != null && p.Id != centerPanelId)
            .ToListAsync();
        var linkSetByPanel = confirmation.Neighbours.ToDictionary(n => n.PanelId);

        foreach (var panel in panels)
        {
            var stagedHolds = await db.Holds
                .Where(h => h.WallPanelId == panel.Id && h.Generation == stagedGen)
                .ToListAsync();
            foreach (var hold in stagedHolds)
            {
                hold.Generation = newGen;
            }

            panel.Photo = panel.StagedPhoto;
            panel.PhotoContentType = panel.StagedPhotoContentType;
            ClearStaged(panel);

            if (!linkSetByPanel.TryGetValue(panel.Id, out var linkSet))
            {
                continue;
            }

            var removed = linkSet.RemovedNeighbourHoldIds.ToHashSet();
            foreach (var link in linkSet.Links)
            {
                if (removed.Contains(link.NewHoldId))
                {
                    continue;
                }

                // link.NeighborHoldId is a staged CENTRE hold: resolve it to the surviving live hold.
                if (!centerStagedToLive.TryGetValue(link.NeighborHoldId, out var centerLiveId))
                {
                    continue;
                }

                db.HoldLinks.Add(new HoldLink
                {
                    WallId = wallId,
                    HoldAId = centerLiveId,
                    HoldBId = link.NewHoldId,
                    Kind = link.Moved ? HoldLinkKind.Moved : HoldLinkKind.Same,
                    CreatedByUserId = userId,
                });
            }

            await DeleteHoldsAsync(db, wallId, removed);
        }
    }

    /// <inheritdoc/>
    public async Task DiscardAsync(Guid wallId)
    {
        var user = await currentUserService.GetCurrentUserAsync();
        await using var db = await dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = user.Id;
        await WallAdminGuard.EnsureWallAdminAsync(db, wallId, user.Id, CancellationToken.None);

        var wall = await db.Walls.FirstOrDefaultAsync(w => w.Id == wallId)
            ?? throw new InvalidOperationException("Wall not found");

        await DiscardStagedAsync(db, wallId, wall.CurrentGeneration + 1);
        await db.SaveChangesAsync();
        logger.LogInformation("Big update discarded on wall {WallId} by {UserId}", wallId, user.Id);
    }

    /// <summary>
    /// Deletes the update-staged panels at <paramref name="stagedGen"/> and their staged holds. Holds
    /// go first because Hold→WallPanel is SetNull: deleting the panel first would strand its staged
    /// holds on the wall instead of removing them. No SaveChanges — the caller commits.
    /// </summary>
    private static async Task DiscardStagedAsync(BlocwerkDbContext db, Guid wallId, int stagedGen)
    {
        var panelIds = await db.WallPanels
            .Where(p => p.WallId == wallId && p.Generation == stagedGen && p.StagedPhoto != null)
            .Select(p => p.Id)
            .ToListAsync();
        if (panelIds.Count == 0)
        {
            return;
        }

        var holds = await db.Holds.Where(h => h.WallPanelId != null && panelIds.Contains(h.WallPanelId.Value)).ToListAsync();
        db.Holds.RemoveRange(holds);
        var panels = await db.WallPanels.Where(p => panelIds.Contains(p.Id)).ToListAsync();
        db.WallPanels.RemoveRange(panels);
    }

    /// <summary>
    /// Deletes one hold that a boulder may point at: clears its <see cref="BoulderHold"/>s (making
    /// their boulders historic), drops the <see cref="HoldLink"/>s referencing it (both ends are
    /// Restrict), then removes the hold. No SaveChanges.
    /// </summary>
    private static async Task RemoveHoldAsync(BlocwerkDbContext db, Hold hold)
    {
        var boulderLinks = await db.BoulderHolds
            .Where(bh => bh.HoldId == hold.Id)
            .Include(bh => bh.Boulder)
            .ToListAsync();
        foreach (var link in boulderLinks)
        {
            if (link.Boulder is { IsArchived: false, IsHistoric: false })
            {
                link.Boulder.IsHistoric = true;
                link.Boulder.NeedsReview = false;
            }
        }

        db.BoulderHolds.RemoveRange(boulderLinks);

        var holdLinks = await db.HoldLinks
            .Where(l => l.HoldAId == hold.Id || l.HoldBId == hold.Id)
            .ToListAsync();
        db.HoldLinks.RemoveRange(holdLinks);

        db.Holds.Remove(hold);
    }

    private static async Task DeleteHoldsAsync(BlocwerkDbContext db, Guid wallId, HashSet<Guid> holdIds)
    {
        if (holdIds.Count == 0)
        {
            return;
        }

        var holds = await db.Holds.Where(h => holdIds.Contains(h.Id) && h.WallId == wallId).ToListAsync();
        foreach (var hold in holds)
        {
            await RemoveHoldAsync(db, hold);
        }
    }

    private static void ClearStaged(WallPanel panel)
    {
        panel.StagedPhoto = null;
        panel.StagedPhotoContentType = null;
        panel.StagedAt = null;
        panel.StagedByUserId = null;
    }

    private static (List<MatcherHold> Holds, Guid[] IndexToGuid) BuildMatcherHolds(IReadOnlyList<Hold> holds)
    {
        var matcher = new List<MatcherHold>(holds.Count);
        var index = new Guid[holds.Count];
        for (var i = 0; i < holds.Count; i++)
        {
            var h = holds[i];
            index[i] = h.Id;
            matcher.Add(new MatcherHold(i, h.X, h.Y, h.Radius));
        }

        return (matcher, index);
    }

    private static HoldOverlapDirection DirectionFromNeighbor(int neighborCol, int neighborRow, int col, int row)
    {
        if (neighborCol < col)
        {
            return HoldOverlapDirection.Right;
        }

        if (neighborCol > col)
        {
            return HoldOverlapDirection.Left;
        }

        if (neighborRow < row)
        {
            return HoldOverlapDirection.Down;
        }

        return HoldOverlapDirection.Up;
    }
}
