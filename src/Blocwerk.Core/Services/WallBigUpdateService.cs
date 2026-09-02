using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

/// <summary>
/// Replaces a wall's photo with a fresh multi-image capture, carrying the old curated holds over
/// onto the new centre photo (so boulders survive) and linking the overlaps between the new panels.
/// Start stages the panels and computes the review session; Promote commits it in one transaction;
/// Discard abandons it. Mutations are gated by <see cref="WallAdminGuard"/>. See
/// <see cref="PromoteAsync"/> in the partial for the boulder-preserving carryover.
/// </summary>
public partial class WallBigUpdateService : IWallBigUpdateService
{
    private readonly IDbContextFactory<BlocwerkDbContext> dbContextFactory;
    private readonly ICurrentUserService currentUserService;
    private readonly IHoldDetectionService holdDetectionService;
    private readonly IHoldOverlapMatcher overlapMatcher;
    private readonly ILogger<WallBigUpdateService> logger;

    public WallBigUpdateService(
        IDbContextFactory<BlocwerkDbContext> dbContextFactory,
        ICurrentUserService currentUserService,
        IHoldDetectionService holdDetectionService,
        IHoldOverlapMatcher overlapMatcher,
        ILogger<WallBigUpdateService> logger)
    {
        this.dbContextFactory = dbContextFactory;
        this.currentUserService = currentUserService;
        this.holdDetectionService = holdDetectionService;
        this.overlapMatcher = overlapMatcher;
        this.logger = logger;
    }

    /// <inheritdoc/>
    public async Task<BigUpdateSession> StartAsync(Guid wallId, IReadOnlyList<BigUpdatePhoto> photos)
    {
        var user = await currentUserService.GetCurrentUserAsync();
        await using var db = await dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = user.Id;
        await WallAdminGuard.EnsureWallAdminAsync(db, wallId, user.Id, CancellationToken.None);

        var wall = await db.Walls.FirstOrDefaultAsync(w => w.Id == wallId)
            ?? throw new InvalidOperationException("Wall not found");

        var centerCount = photos.Count(p => p.Col == 0 && p.Row == 0);
        if (centerCount != 1)
        {
            throw new InvalidOperationException("Exactly one centre photo at (0,0) is required.");
        }

        if (photos.Select(p => (p.Col, p.Row)).Distinct().Count() != photos.Count)
        {
            throw new InvalidOperationException("Two photos share the same grid position.");
        }

        if (wall.Photo is null)
        {
            throw new InvalidOperationException("Wall has no live photo to carry holds over from.");
        }

        var stagedGen = wall.CurrentGeneration + 1;

        // Idempotent restart: drop any previous in-flight update-staged panels + holds first.
        await DiscardStagedAsync(db, wallId, stagedGen);
        await db.SaveChangesAsync();

        Guid centerPanelId = Guid.Empty;
        foreach (var photo in photos)
        {
            // Stored exactly as uploaded: hold detection and the panel matcher below must see the
            // camera's full resolution. The browser is served downscaled variants instead, generated
            // on demand from these originals (see IImageVariantCache).
            var image = photo.Image;
            var contentType = photo.ContentType;

            var panel = new WallPanel
            {
                WallId = wallId,
                Col = photo.Col,
                Row = photo.Row,
                Photo = null,
                StagedPhoto = image,
                StagedPhotoContentType = contentType,
                StagedAt = DateTimeOffset.UtcNow,
                StagedByUserId = user.Id,
                Generation = stagedGen,
            };
            db.WallPanels.Add(panel);
            if (photo.Col == 0 && photo.Row == 0)
            {
                centerPanelId = panel.Id;
            }

            var detected = await holdDetectionService.DetectHoldsAsync(image);
            foreach (var d in detected)
            {
                db.Holds.Add(new Hold
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
                    Generation = stagedGen,
                });
            }
        }

        await db.SaveChangesAsync();

        var session = await BuildSessionAsync(db, wall, centerPanelId, stagedGen);
        logger.LogInformation(
            "Big update started on wall {WallId} by {UserId}: {Carry} carryover, {Removed} removal candidates, {New} new-centre, {Panels} neighbour panels",
            wallId, user.Id, session.Carryover.Count, session.RemovedCandidateHoldIds.Count,
            session.NewCenterHoldIds.Count, session.Neighbours.Count);
        return session;
    }

    /// <inheritdoc/>
    public async Task<BigUpdateSession> ResumeAsync(Guid wallId)
    {
        var user = await currentUserService.GetCurrentUserAsync();
        await using var db = await dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = user.Id;
        await WallAdminGuard.EnsureWallAdminAsync(db, wallId, user.Id, CancellationToken.None);

        var wall = await db.Walls.FirstOrDefaultAsync(w => w.Id == wallId)
            ?? throw new InvalidOperationException("Wall not found");

        var stagedGen = wall.CurrentGeneration + 1;
        var centerPanel = await db.WallPanels.FirstOrDefaultAsync(p =>
            p.WallId == wallId && p.Col == 0 && p.Row == 0
            && p.Generation == stagedGen && p.StagedPhoto != null)
            ?? throw new InvalidOperationException("No in-flight big update to resume.");

        var session = await BuildSessionAsync(db, wall, centerPanel.Id, stagedGen);
        logger.LogInformation(
            "Big update resumed on wall {WallId} by {UserId}: {Carry} carryover, {Panels} neighbour panels",
            wallId, user.Id, session.Carryover.Count, session.Neighbours.Count);
        return session;
    }

    /// <summary>
    /// Runs the two matcher passes over the already-persisted staged panels and holds: the old live
    /// holds against the staged centre (carryover), and every non-centre staged panel against the
    /// staged centre (overlap). Shared by <see cref="StartAsync"/> and <see cref="ResumeAsync"/> so
    /// both yield an identical session for the same DB state. No detection, no mutation.
    /// </summary>
    private async Task<BigUpdateSession> BuildSessionAsync(
        BlocwerkDbContext db, Wall wall, Guid centerPanelId, int stagedGen)
    {
        var oldHolds = await db.Holds
            .Where(h => h.WallId == wall.Id && h.Generation == wall.CurrentGeneration)
            .ToListAsync();
        var centerHolds = await db.Holds
            .Where(h => h.WallPanelId == centerPanelId && h.Generation == stagedGen)
            .ToListAsync();
        var centerImage = await db.WallPanels
            .Where(p => p.Id == centerPanelId)
            .Select(p => p.StagedPhoto)
            .FirstAsync()
            ?? throw new InvalidOperationException("Centre panel has no staged photo.");

        var (oldMatcher, oldIndex) = BuildMatcherHolds(oldHolds);
        var (centerMatcher, centerIndex) = BuildMatcherHolds(centerHolds);

        var carryover = new List<CarryoverProposal>();
        var removedCandidates = new List<Guid>();
        var newCenter = new List<Guid>();
        try
        {
            var carry = overlapMatcher.Match(
                wall.Photo!, oldMatcher, centerImage, centerMatcher, HoldOverlapDirection.Right);
            foreach (var p in carry.Proposals)
            {
                carryover.Add(new CarryoverProposal(
                    oldIndex[p.LeftHoldId], centerIndex[p.RightHoldId], p.Confidence, p.Moved, p.ResidualPx));
            }

            removedCandidates.AddRange(carry.UnmatchedLeft.Select(i => oldIndex[i]));
            newCenter.AddRange(carry.UnmatchedRight.Select(i => centerIndex[i]));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Carryover match failed on wall {WallId}; treating all old holds as undecided", wall.Id);
        }

        var neighbours = new List<NeighbourOverlap>();
        var stagedPanels = await db.WallPanels
            .Where(p => p.WallId == wall.Id && p.Generation == stagedGen
                && p.StagedPhoto != null && p.Id != centerPanelId)
            .Select(p => new { p.Id, p.Col, p.Row, p.StagedPhoto })
            .ToListAsync();

        foreach (var panel in stagedPanels)
        {
            var neighbourHolds = await db.Holds
                .Where(h => h.WallPanelId == panel.Id && h.Generation == stagedGen)
                .ToListAsync();
            var (neighbourMatcher, neighbourIndex) = BuildMatcherHolds(neighbourHolds);
            var direction = DirectionFromNeighbor(0, 0, panel.Col, panel.Row);

            var proposals = new List<OverlapProposalDto>();
            try
            {
                var result = overlapMatcher.Match(
                    centerImage, centerMatcher, panel.StagedPhoto!, neighbourMatcher, direction);
                foreach (var p in result.Proposals)
                {
                    proposals.Add(new OverlapProposalDto(
                        panel.Id, centerIndex[p.LeftHoldId], neighbourIndex[p.RightHoldId],
                        p.Confidence, p.Moved, p.ResidualPx));
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Overlap match failed for panel {PanelId} on wall {WallId}", panel.Id, wall.Id);
            }

            neighbours.Add(new NeighbourOverlap(panel.Id, panel.Col, panel.Row, proposals));
        }

        return new BigUpdateSession(wall.Id, centerPanelId, carryover, removedCandidates, newCenter, neighbours);
    }
}
