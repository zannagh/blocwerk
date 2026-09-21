using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

/// <summary>
/// Big-wall multi-image topology and the stage → confirm → discard lifecycle of adding a panel,
/// including cross-panel hold re-recognition via <see cref="IHoldOverlapMatcher"/>. Mutations are
/// gated by <see cref="WallAdminGuard"/>; reads only require the wall to be visible to the caller.
/// </summary>
public partial class WallPanelService : IWallPanelService
{
    private readonly IDbContextFactory<BlocwerkDbContext> dbContextFactory;
    private readonly ICurrentUserService currentUserService;
    private readonly IHoldDetectionService holdDetectionService;
    private readonly IHoldOverlapMatcher overlapMatcher;
    private readonly ILogger<WallPanelService> logger;
    private readonly IKioskContext? kioskContext;
    private readonly IChangeJournal? changeJournal;

    /// <summary>Creates the service.</summary>
    /// <remarks>
    /// <c>kioskContext</c> is optional, as on <c>WallService</c>: hosts with no HTTP layer never
    /// register one, which means "never a kiosk". It only ever LOOSENS the read-only queries in
    /// <c>WallPanelService.Reads</c> for an anonymous kiosk on its own wall, so its absence fails
    /// closed and no write path consults it at all.
    /// <para>
    /// <c>changeJournal</c> is optional for the same reason it is on <c>WallService</c>: unit tests
    /// construct the service without one. It only ever NAMES the batch a SaveChanges lands in — the
    /// journalling itself is done by the DbContext interceptor — so its absence changes no behaviour
    /// beyond the deletes ending up in an anonymous "adhoc" batch.
    /// </para>
    /// </remarks>
    public WallPanelService(
        IDbContextFactory<BlocwerkDbContext> dbContextFactory,
        ICurrentUserService currentUserService,
        IHoldDetectionService holdDetectionService,
        IHoldOverlapMatcher overlapMatcher,
        ILogger<WallPanelService> logger,
        IKioskContext? kioskContext = null,
        IChangeJournal? changeJournal = null)
    {
        this.dbContextFactory = dbContextFactory;
        this.currentUserService = currentUserService;
        this.holdDetectionService = holdDetectionService;
        this.overlapMatcher = overlapMatcher;
        this.logger = logger;
        this.kioskContext = kioskContext;
        this.changeJournal = changeJournal;
    }

    /// <inheritdoc/>
    public async Task<StagePanelResult> StagePanelAsync(Guid wallId, int col, int row, byte[] image, string contentType)
    {
        // Stored unmodified: the panel matcher and hold detection below need the full-resolution
        // upload. The browser gets downscaled variants derived from it (see IImageVariantCache).
        var user = await currentUserService.GetCurrentUserAsync();
        await using var db = await dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = user.Id;
        await WallAdminGuard.EnsureWallAdminAsync(db, wallId, user.Id, CancellationToken.None);

        var wall = await db.Walls.FirstOrDefaultAsync(w => w.Id == wallId);
        if (wall is null)
        {
            throw new InvalidOperationException("Wall not found");
        }

        var placements = await db.WallPanels
            .AsNoTracking()
            .Where(p => p.WallId == wallId)
            .Select(p => new { p.Id, p.Col, p.Row, Live = p.Photo != null })
            .ToListAsync();

        if (placements.Any(p => p.Col == col && p.Row == row))
        {
            throw new InvalidOperationException($"A panel already occupies ({col},{row}).");
        }

        var neighborSet = Neighbors(col, row).ToHashSet();
        var adjacentLive = placements
            .Where(p => p.Live && neighborSet.Contains((p.Col, p.Row)))
            .ToList();
        if (adjacentLive.Count == 0)
        {
            throw new InvalidOperationException(
                $"({col},{row}) is not orthogonally adjacent to any live panel.");
        }

        var panel = new WallPanel
        {
            WallId = wallId,
            Col = col,
            Row = row,
            Photo = null,
            StagedPhoto = image,
            StagedPhotoContentType = contentType,
            StagedAt = DateTimeOffset.UtcNow,
            StagedByUserId = user.Id,
            Generation = wall.CurrentGeneration,
        };
        db.WallPanels.Add(panel);

        var detected = await holdDetectionService.DetectHoldsAsync(image);
        var newHolds = new List<Hold>(detected.Count);
        foreach (var d in detected)
        {
            var hold = new Hold
            {
                WallId = wallId,
                WallPanelId = panel.Id,
                X = d.X,
                Y = d.Y,
                Radius = d.Radius,
                Color = d.Color,
                Confidence = d.Confidence,
                IsAutoDetected = true,
                NeedsReview = true,
                Generation = wall.CurrentGeneration,
            };
            newHolds.Add(hold);
            db.Holds.Add(hold);
        }

        await db.SaveChangesAsync();

        var proposals = await BuildOverlapProposalsAsync(db, wall, panel, image, newHolds);

        logger.LogInformation(
            "Panel {PanelId} staged on wall {WallId} at ({Col},{Row}): {HoldCount} holds, {ProposalCount} proposals",
            panel.Id, wallId, col, row, newHolds.Count, proposals.Count);
        return new StagePanelResult(panel.Id, proposals);
    }

    /// <inheritdoc/>
    public async Task<StagePanelResult> ResumePanelAsync(Guid wallId, Guid panelId)
    {
        var user = await currentUserService.GetCurrentUserAsync();
        await using var db = await dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = user.Id;
        await WallAdminGuard.EnsureWallAdminAsync(db, wallId, user.Id, CancellationToken.None);

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

        if (panel.StagedPhoto is null || panel.Photo is not null)
        {
            throw new InvalidOperationException("Panel is not staged.");
        }

        var panelHolds = await db.Holds
            .Where(h => h.WallPanelId == panelId && h.Generation == wall.CurrentGeneration)
            .ToListAsync();

        var proposals = await BuildOverlapProposalsAsync(db, wall, panel, panel.StagedPhoto, panelHolds);

        logger.LogInformation(
            "Panel {PanelId} resumed on wall {WallId} at ({Col},{Row}): {HoldCount} holds, {ProposalCount} proposals",
            panelId, wallId, panel.Col, panel.Row, panelHolds.Count, proposals.Count);
        return new StagePanelResult(panelId, proposals);
    }

    /// <summary>
    /// Matches a panel's holds against every orthogonally-adjacent live neighbour and returns the
    /// overlap proposals (neighbour hold ↔ new hold candidates). Shared by staging a fresh panel
    /// and resuming a stranded one, so both paths yield identical proposals for the same DB state.
    /// A neighbour whose match throws is logged and skipped rather than failing the whole build.
    /// </summary>
    private async Task<List<OverlapProposalDto>> BuildOverlapProposalsAsync(
        BlocwerkDbContext db,
        Wall wall,
        WallPanel panel,
        byte[] image,
        IReadOnlyList<Hold> panelHolds)
    {
        var neighborSet = Neighbors(panel.Col, panel.Row).ToHashSet();
        var adjacentLive = await db.WallPanels
            .AsNoTracking()
            .Where(p => p.WallId == wall.Id && p.Photo != null && p.Id != panel.Id)
            .Select(p => new { p.Id, p.Col, p.Row })
            .ToListAsync();

        var (newMatcherHolds, newIndex) = BuildMatcherHolds(panelHolds);
        var proposals = new List<OverlapProposalDto>();
        foreach (var neighbor in adjacentLive.Where(p => neighborSet.Contains((p.Col, p.Row))))
        {
            var neighborImage = await db.WallPanels
                .Where(p => p.Id == neighbor.Id)
                .Select(p => p.Photo)
                .FirstOrDefaultAsync();
            if (neighborImage is null)
            {
                continue;
            }

            var neighborHolds = await db.Holds
                .Where(h => h.WallPanelId == neighbor.Id && h.Generation == wall.CurrentGeneration)
                .ToListAsync();
            var (neighborMatcherHolds, neighborIndex) = BuildMatcherHolds(neighborHolds);

            var direction = DirectionFromNeighbor(neighbor.Col, neighbor.Row, panel.Col, panel.Row);
            try
            {
                var result = overlapMatcher.Match(
                    neighborImage, neighborMatcherHolds, image, newMatcherHolds, direction);
                foreach (var p in result.Proposals)
                {
                    proposals.Add(new OverlapProposalDto(
                        neighbor.Id,
                        neighborIndex[p.LeftHoldId],
                        newIndex[p.RightHoldId],
                        p.Confidence,
                        p.Moved,
                        p.ResidualPx));
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex, "Overlap match failed for panel {PanelId} vs neighbour {NeighborId}",
                    panel.Id, neighbor.Id);
            }
        }

        return proposals;
    }

    /// <inheritdoc/>
    public async Task ConfirmPanelAsync(
        Guid wallId,
        Guid panelId,
        IReadOnlyList<ConfirmedLink> links,
        IReadOnlyList<Guid> removedNeighborHoldIds)
    {
        var user = await currentUserService.GetCurrentUserAsync();
        await using var db = await dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = user.Id;
        await WallAdminGuard.EnsureWallAdminAsync(db, wallId, user.Id, CancellationToken.None);

        var panel = await db.WallPanels.FirstOrDefaultAsync(p => p.Id == panelId && p.WallId == wallId);
        if (panel is null)
        {
            throw new InvalidOperationException("Panel not found");
        }

        var removed = removedNeighborHoldIds.ToHashSet();
        await AddConfirmedLinksAsync(db, wallId, links, removed, user.Id);
        await DeleteRemovedNeighborHoldsAsync(db, wallId, removed, logger);

        panel.Photo = panel.StagedPhoto;
        panel.PhotoContentType = panel.StagedPhotoContentType;
        panel.StagedPhoto = null;
        panel.StagedPhotoContentType = null;
        panel.StagedAt = null;
        panel.StagedByUserId = null;

        // A new photo went live on this panel, so whatever an editor previously declared "linked up"
        // no longer covers the wall: bring the linking callout back for this generation.
        await ClearLinksFinalizedAsync(db, wallId, CancellationToken.None);

        // A named batch: this one SaveChanges promotes the panel AND hard-deletes the neighbour holds
        // the user flagged as removed (making their boulders historic). That is the most destructive
        // thing this service does, so it must be findable in the journal for a later revert instead of
        // landing in an anonymous adhoc batch.
        using (changeJournal?.BeginBatch("panel-confirm", ChangeJournalScopeKind.Wall, wallId))
        {
            await db.SaveChangesAsync();
        }

        logger.LogInformation("Panel {PanelId} confirmed live on wall {WallId} by {UserId}", panelId, wallId, user.Id);
    }

    /// <summary>
    /// Adds the cross-panel <see cref="HoldLink"/>s the user confirmed, skipping any that touch a hold
    /// marked as removed and any that already exist (the unique pair is unordered). No SaveChanges —
    /// the caller commits them atomically with the removals and the panel promotion.
    /// </summary>
    private static async Task AddConfirmedLinksAsync(
        BlocwerkDbContext db,
        Guid wallId,
        IReadOnlyList<ConfirmedLink> links,
        HashSet<Guid> removed,
        Guid userId)
    {
        var existing = await db.HoldLinks
            .Where(l => l.WallId == wallId)
            .Select(l => new { l.HoldAId, l.HoldBId })
            .ToListAsync();
        var seen = existing.Select(e => Unordered(e.HoldAId, e.HoldBId)).ToHashSet();

        foreach (var link in links)
        {
            // Removal wins: never link a hold the user marked as gone (defends against a stale
            // decision even though the stepper already filters these out).
            if (removed.Contains(link.NeighborHoldId) || removed.Contains(link.NewHoldId))
            {
                continue;
            }

            if (!seen.Add(Unordered(link.NeighborHoldId, link.NewHoldId)))
            {
                continue;
            }

            db.HoldLinks.Add(new HoldLink
            {
                WallId = wallId,
                HoldAId = link.NeighborHoldId,
                HoldBId = link.NewHoldId,
                Kind = link.Moved ? HoldLinkKind.Moved : HoldLinkKind.Same,
                CreatedByUserId = userId,
            });
        }
    }

    /// <summary>
    /// Deletes the neighbour holds the user flagged as physically removed from the wall. Everything
    /// referencing them with a Restrict FK is cleared first (<see cref="HoldDeletion"/>): boulder
    /// memberships — their boulders are made historic — panel links, and the cross-generation
    /// lineage, whose dying end is tombstoned. Only holds belonging to <paramref name="wallId"/> are
    /// touched. No SaveChanges — the caller commits removals atomically with the new links and the
    /// panel promotion.
    /// </summary>
    private static async Task DeleteRemovedNeighborHoldsAsync(
        BlocwerkDbContext db, Guid wallId, HashSet<Guid> removed, ILogger<WallPanelService> logger)
    {
        if (removed.Count == 0)
        {
            return;
        }

        var holds = await db.Holds
            .Where(h => removed.Contains(h.Id) && h.WallId == wallId)
            .ToListAsync();
        var retired = await HoldDeletion.PrepareHoldsForDeleteAsync(db, holds.Select(h => h.Id).ToList());
        db.Holds.RemoveRange(holds);

        // Retiring a boulder is the loudest thing this path does and the user is never shown it, so at
        // least say it happened: without the count the only trace is the boulder quietly going historic.
        if (retired > 0)
        {
            logger.LogInformation(
                "Removing {HoldCount} neighbour holds on wall {WallId} retired {BoulderCount} boulders",
                holds.Count, wallId, retired);
        }
    }

    /// <inheritdoc/>
    public async Task DiscardPanelAsync(Guid wallId, Guid panelId)
    {
        var user = await currentUserService.GetCurrentUserAsync();
        await using var db = await dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = user.Id;
        await WallAdminGuard.EnsureWallAdminAsync(db, wallId, user.Id, CancellationToken.None);

        var panel = await db.WallPanels.FirstOrDefaultAsync(p => p.Id == panelId && p.WallId == wallId);
        if (panel is null)
        {
            throw new InvalidOperationException("Panel not found");
        }

        // Delete the panel's holds FIRST: Hold→WallPanel is SetNull on delete, so removing the
        // panel first would orphan its staged holds onto the center wall instead of deleting them.
        var holds = await db.Holds.Where(h => h.WallPanelId == panelId).ToListAsync();

        // A discarded panel's holds are staged rows with no memberships; panel links and lineage are
        // cleared defensively so the discard can never roll back on a Restrict FK. Memberships stay
        // untouched on purpose — if one exists the delete should fail loudly, not retire a boulder.
        await HoldDeletion.PrepareHoldsForDeleteAsync(
            db, holds.Select(h => h.Id).ToList(), HoldDeleteBoulderPolicy.LeaveUntouched);
        db.Holds.RemoveRange(holds);
        await db.SaveChangesAsync();

        db.WallPanels.Remove(panel);
        await db.SaveChangesAsync();
        logger.LogInformation("Panel {PanelId} discarded on wall {WallId} by {UserId}", panelId, wallId, user.Id);
    }
}
