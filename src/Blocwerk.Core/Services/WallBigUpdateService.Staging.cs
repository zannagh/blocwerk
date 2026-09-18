using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

/// <summary>
/// The staging half of the big-wall update: validating the uploaded capture, persisting the staged
/// panels and running hold detection on them — everything BEFORE the matcher. Matching lives in the
/// main partial (<see cref="ResumeAsync"/> / BuildSessionAsync) and runs as a separate step, so the
/// user can correct the detection first and have those corrections feed the proposals.
/// </summary>
public partial class WallBigUpdateService
{
    /// <inheritdoc/>
    public async Task<BigUpdateSession> StageAsync(Guid wallId, IReadOnlyList<BigUpdatePhoto> photos)
    {
        var user = await currentUserService.GetCurrentUserAsync();
        await using var db = await dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = user.Id;
        await WallAdminGuard.EnsureWallAdminAsync(db, wallId, user.Id, CancellationToken.None);

        var wall = await db.Walls.FirstOrDefaultAsync(w => w.Id == wallId)
            ?? throw new InvalidOperationException("Wall not found");

        ValidateStagedPhotos(wall, photos);

        var stagedGen = wall.CurrentGeneration + 1;

        // Open the wall-update batch here (after validation, so a rejected start records nothing) and hold
        // it for the whole method: the restart-discard and the staging INSERTs below both land in it, and
        // PromoteAsync later RESUMES this same open batch. That makes the staged-hold creations part of the
        // one unit the promote seals — so reverting/replaying the update is self-contained. Null in tests.
        using var journalBatch = changeJournal?.BeginWallUpdateBatch(wallId);

        // Idempotent restart: drop any previous in-flight update-staged panels + holds first.
        await DiscardStagedAsync(db, wallId, stagedGen);
        await db.SaveChangesAsync();

        var centerPanelId = await StageAndDetectAsync(db, wallId, photos, stagedGen, user.Id);
        await db.SaveChangesAsync();

        // Stop at the staged state: the detections are persisted but NOT yet matched. The user reviews
        // and corrects them first (pre-match touch-up), and the matcher runs afterwards over the
        // corrected rows via ResumeAsync — so an added/moved/deleted hold feeds the carryover and
        // overlap proposals instead of being fixed up after the fact.
        var session = await BuildStagedSessionAsync(db, wall.Id, centerPanelId, stagedGen);
        logger.LogInformation(
            "Big update staged on wall {WallId} by {UserId}: {Panels} staged panel(s), matching deferred",
            wallId, user.Id, session.Neighbours.Count + 1);
        return session;
    }

    /// <summary>
    /// Rejects an upload that cannot be staged, BEFORE anything is written or journalled: an empty set,
    /// two photos on the same grid position, a grid that is not closed toward the centre (see below), or
    /// a wall with no live photo to carry holds over from.
    /// </summary>
    private static void ValidateStagedPhotos(Wall wall, IReadOnlyList<BigUpdatePhoto> photos)
    {
        if (photos.Count == 0)
        {
            throw new InvalidOperationException("At least one photo is required.");
        }

        var stagedPositions = photos.Select(p => (p.Col, p.Row)).ToHashSet();
        if (stagedPositions.Count != photos.Count)
        {
            throw new InvalidOperationException("Two photos share the same grid position.");
        }

        // Center-first (decision D-D): a non-centre panel may be re-photographed only together with the
        // panel one step toward the centre, so an outer panel never advances past a more-central one and
        // strands it a generation behind (a "hole"). Because every subset promote bumps the wall's
        // generation and no live panel is ever ahead of it, the inner neighbour can only reach the new
        // target generation by being part of THIS update — a live neighbour is at most the old generation.
        // So the staged set must be closed toward (0,0), which also guarantees the centre is always
        // present as the carryover anchor the rest of the flow relies on.
        foreach (var (col, row) in stagedPositions)
        {
            if (col == 0 && row == 0)
            {
                continue;
            }

            var toward = StepTowardCentre(col, row);
            if (!stagedPositions.Contains(toward))
            {
                throw new InvalidOperationException(
                    $"Center-first update: panel ({col},{row}) can only be updated together with its more-central "
                    + $"neighbour ({toward.Col},{toward.Row}). Re-photograph outward from the centre.");
            }
        }

        if (wall.Photo is null)
        {
            throw new InvalidOperationException("Wall has no live photo to carry holds over from.");
        }
    }

    /// <summary>
    /// Adds one staged <see cref="WallPanel"/> per uploaded photo and runs hold detection on each, adding
    /// the detections as staged holds at <paramref name="stagedGen"/>. Returns the centre (0,0) panel's id.
    /// Nothing is saved here — the caller commits.
    /// </summary>
    private async Task<Guid> StageAndDetectAsync(
        BlocwerkDbContext db,
        Guid wallId,
        IReadOnlyList<BigUpdatePhoto> photos,
        int stagedGen,
        Guid userId)
    {
        var centerPanelId = Guid.Empty;
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
                StagedByUserId = userId,
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

        return centerPanelId;
    }

    /// <inheritdoc/>
    public async Task<BigUpdateSession> GetStagedAsync(Guid wallId)
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

        return await BuildStagedSessionAsync(db, wallId, centerPanel.Id, stagedGen);
    }

    /// <summary>
    /// The pre-match view of the staged update: the centre panel plus every staged neighbour, each with
    /// no proposals. Enough for the hold-level review surfaces (which only need the staged panel ids)
    /// while costing nothing — no image loading, no matcher pass.
    /// </summary>
    private static async Task<BigUpdateSession> BuildStagedSessionAsync(
        BlocwerkDbContext db, Guid wallId, Guid centerPanelId, int stagedGen)
    {
        var neighbours = await db.WallPanels
            .Where(p => p.WallId == wallId && p.Generation == stagedGen
                && p.StagedPhoto != null && p.Id != centerPanelId)
            .OrderBy(p => p.Row).ThenBy(p => p.Col)
            .Select(p => new NeighbourOverlap(p.Id, p.Col, p.Row, new List<OverlapProposalDto>()))
            .ToListAsync();

        return new BigUpdateSession(wallId, centerPanelId, [], [], [], neighbours);
    }
}
