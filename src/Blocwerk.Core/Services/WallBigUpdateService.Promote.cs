using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

/// <summary>
/// The commit and abandon halves of the big-wall update, plus the pure helpers shared with the
/// session builder. Under immutable generations, promotion RETAINS the old gen-N hold rows as history:
/// each carried hold gets a distinct gen-N+1 row (the promoted staged twin or a clone), a
/// <see cref="HoldGenerationLink"/> ties old to new, and the advancing boulders' <see cref="BoulderHold"/>s
/// are re-pointed onto the new row so boulders survive. See <see cref="CarryCentreHoldsAsync"/> (partial).
/// </summary>
public partial class WallBigUpdateService
{
    /// <inheritdoc/>
    public async Task PromoteAsync(Guid wallId, BigUpdateConfirmation confirmation)
    {
        // Resume the SAME open wall-update batch the run (StartAsync) opened, so the staged-hold INSERTs
        // and this promote's carry writes are ONE self-contained, revertible/replayable unit — reverting
        // it undoes the staged-hold creations too, and replaying it recreates them. Null when the journal
        // isn't wired (e.g. unit tests); capture then falls back to per-SaveChanges adhoc batches.
        using var journalBatch = changeJournal?.BeginWallUpdateBatch(wallId);

        var user = await currentUserService.GetCurrentUserAsync();
        await using var db = await dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = user.Id;
        await WallAdminGuard.EnsureWallAdminAsync(db, wallId, user.Id, CancellationToken.None);

        var wall = await db.Walls.FirstOrDefaultAsync(w => w.Id == wallId)
            ?? throw new InvalidOperationException("Wall not found");

        var oldGen = wall.CurrentGeneration;
        var newGen = oldGen + 1;
        var stagedGen = newGen;

        var centerPanel = await db.WallPanels.FirstOrDefaultAsync(p =>
            p.WallId == wallId && p.Col == 0 && p.Row == 0
            && p.Generation == stagedGen && p.StagedPhoto != null)
            ?? throw new InvalidOperationException("No in-flight big update to promote.");

        // Subset promote: only the re-photographed panels advance. Scope the carryover to old holds that
        // live on an updated panel — the holds on panels not re-shot are left entirely alone (they stay
        // at their generation and their panels derive IsOutdated from the bumped wall generation).
        var updatedPositions = await LoadUpdatedPositionsAsync(db, wallId, stagedGen);
        var panelPositions = await LoadPanelPositionsAsync(db, wallId);

        var carriedOldHolds = (await db.Holds
                .Where(h => h.WallId == wallId && h.Generation == oldGen)
                .ToListAsync())
            .Where(h => IsOnUpdatedPanel(h.WallPanelId, panelPositions, updatedPositions))
            .ToList();

        // Drop crash-mat / floor false holds BEFORE the carry so a mat accepted at an earlier generation
        // never gets a successor or a lineage link (the boulder-membership guard keeps referenced holds).
        var oldHolds = (await FilterCarriedMatFalseHoldsAsync(db, carriedOldHolds))
            .ToDictionary(h => h.Id);
        var centerStaged = await db.Holds
            .Where(h => h.WallPanelId == centerPanel.Id && h.Generation == stagedGen)
            .ToDictionaryAsync(h => h.Id);

        // Map each updated grid position to its NEW-generation staged panel row, so a carried old hold on
        // ANY re-shot panel (the centre OR a co-updated neighbour) advances/clones onto the new-generation
        // row of ITS OWN position — never onto the centre. Without this, an updated neighbour's old holds
        // (which land in oldHolds via IsOnUpdatedPanel but have no twin in the centre-only centerStaged)
        // fell to the clone branch and were cloned onto the CENTRE panel. Built before ClearStaged and the
        // neighbour promote, while every staged row still carries its StagedPhoto at stagedGen.
        var newGenPanelByPosition = (await db.WallPanels
                .Where(p => p.WallId == wallId && p.Generation == stagedGen && p.StagedPhoto != null)
                .Select(p => new { p.Id, p.Col, p.Row })
                .ToListAsync())
            .ToDictionary(p => (p.Col, p.Row), p => p.Id);

        // Twin lookup spanning EVERY updated panel's staged detections, not just the centre: a co-updated
        // neighbour's old hold twin-matches a fresh detection on ITS OWN panel (the per-panel pass in
        // BuildSessionAsync produces that proposal), so it promotes that twin IN PLACE instead of cloning.
        // Paired with PromoteNeighboursAsync keeping the fresh detections, the centre-only lookup used to
        // clone every neighbour old AND keep its detection — doubling every co-updated neighbour hold.
        var updatedPanelIds = newGenPanelByPosition.Values.ToHashSet();
        var stagedTwins = await db.Holds
            .Where(h => h.Generation == stagedGen && h.WallPanelId != null
                && updatedPanelIds.Contains(h.WallPanelId.Value))
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

        // CENTRE carryover (immutable generations): retain the old gen-N rows, promote/clone distinct
        // gen-N+1 rows, re-point the memberships on the re-shot holds, and record the lineage links.
        // Returns the staged centre holds that went live — now an identity set, since a promoted twin
        // keeps its own id.
        var survivingCenterStaged = await CarryCentreHoldsAsync(
            db, wallId, centerPanel, oldGen, newGen, oldHolds, stagedTwins, centerStaged, confirmation,
            confirmation.CarriedWarpPositions, confirmation.CarriedWarpShapes, panelPositions,
            newGenPanelByPosition, user.Id);

        // Centre panel goes live at (0,0).
        centerPanel.Photo = centerPanel.StagedPhoto;
        centerPanel.PhotoContentType = centerPanel.StagedPhotoContentType;
        ClearStaged(centerPanel);

        await PromoteNeighboursAsync(db, wallId, stagedGen, newGen, centerPanel.Id, survivingCenterStaged, confirmation, user.Id);

        wall.Photo = centerPanel.Photo;
        wall.PhotoContentType = centerPanel.PhotoContentType;
        wall.UsesMultipleImages = true;
        wall.CurrentGeneration = newGen;
        wall.LastResetAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync();

        // Seal the batch now that the update is a complete, closed unit: the next update on this wall opens
        // a fresh batch instead of appending to this one. Sealing after the successful SaveChanges means a
        // failed promote leaves the batch OPEN, so a retry resumes it rather than orphaning the staging.
        if (changeJournal is not null)
        {
            await changeJournal.SealWallUpdateBatchAsync(wallId);
        }

        logger.LogInformation(
            "Big update promoted on wall {WallId} by {UserId}: generation {Old}->{New}", wallId, user.Id, oldGen, newGen);
    }

    /// <summary>
    /// Brings every non-centre staged panel live, promotes its staged holds to the new generation,
    /// applies the user's removals, and creates the confirmed hold links. A promoted staged centre hold
    /// now keeps its own id (it IS the live row), so a neighbour link resolves directly to its centre
    /// end — kept only when that centre hold went live, dropped when it was discarded. No SaveChanges:
    /// the caller commits atomically.
    /// </summary>
    private static async Task PromoteNeighboursAsync(
        BlocwerkDbContext db,
        Guid wallId,
        int stagedGen,
        int newGen,
        Guid centerPanelId,
        IReadOnlySet<Guid> survivingCenterStaged,
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
                // A staged detection the carry already consumed as an old hold's twin is live in place
                // (promoted by AdvanceCarriedHoldAsync). Promote only the NON-twin fresh detections here —
                // mirroring how ReconcileNewCentreHolds skips consumed centre twins — so a neighbour hold
                // is never both carried (in place) and independently re-promoted. Every gen-N+1 hold on the
                // panel appears exactly once.
                if (survivingCenterStaged.Contains(hold.Id))
                {
                    continue;
                }

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

                // link.NeighborHoldId is a staged CENTRE hold, which IS its own live row now: keep the
                // link only if that centre hold went live; drop it if the centre hold was discarded.
                if (!survivingCenterStaged.Contains(link.NeighborHoldId))
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

        // Record the staged-row DELETEs into (and then seal) the same open wall-update batch the run
        // opened, so a discarded staging is a closed, net-zero unit — its staging INSERTs and these
        // DELETEs cancel out — that cannot be resumed and leaves no dangling open batch or orphan staged
        // holds in the journal. Null in unit tests; the delete behaviour below is unchanged either way.
        using (var journalBatch = changeJournal?.BeginWallUpdateBatch(wallId))
        {
            await DiscardStagedAsync(db, wallId, wall.CurrentGeneration + 1);
            await db.SaveChangesAsync();
        }

        if (changeJournal is not null)
        {
            await changeJournal.SealWallUpdateBatchAsync(wallId);
        }

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

            // Pass the custom outline (when present) as ABSOLUTE normalized vertices: ShapePoints are
            // centre-relative offsets, so the absolute vertex is (X + Dx, Y + Dy). Lets the matcher warp
            // the polygon onto the new image. Holds with no/degenerate outline stay plain circles (null).
            IReadOnlyList<(double X, double Y)>? shape = h.ShapePoints is { Count: >= 3 }
                ? h.ShapePoints.Select(sp => (h.X + sp.Dx, h.Y + sp.Dy)).ToList()
                : null;
            matcher.Add(new MatcherHold(i, h.X, h.Y, h.Radius, Shape: shape));
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

    /// <summary>
    /// The grid position one step from (<paramref name="col"/>,<paramref name="row"/>) toward the centre
    /// (0,0): reduce the column toward zero first, then the row. Only meaningful for a non-centre panel;
    /// used by the center-first rule to find the more-central neighbour that must be updated alongside it.
    /// </summary>
    private static (int Col, int Row) StepTowardCentre(int col, int row)
    {
        if (col > 0)
        {
            return (col - 1, row);
        }

        if (col < 0)
        {
            return (col + 1, row);
        }

        if (row > 0)
        {
            return (col, row - 1);
        }

        return (col, row + 1);
    }

    /// <summary>
    /// The grid positions re-photographed in THIS update: the staged panels at <paramref name="stagedGen"/>.
    /// A subset promote touches only holds and boulders on these positions; panels not re-shot are left
    /// entirely at their current generation. The centre (0,0) is always among them (the center-first rule
    /// in <see cref="StartAsync"/> guarantees it), so the carryover always has a centre anchor.
    /// </summary>
    private static async Task<HashSet<(int Col, int Row)>> LoadUpdatedPositionsAsync(
        BlocwerkDbContext db, Guid wallId, int stagedGen)
    {
        var positions = await db.WallPanels
            .Where(p => p.WallId == wallId && p.Generation == stagedGen && p.StagedPhoto != null)
            .Select(p => new { p.Col, p.Row })
            .ToListAsync();
        return positions.Select(p => (p.Col, p.Row)).ToHashSet();
    }

    /// <summary>Every panel of the wall by id → its grid position, for resolving a hold's (Col,Row).</summary>
    private static async Task<Dictionary<Guid, (int Col, int Row)>> LoadPanelPositionsAsync(
        BlocwerkDbContext db, Guid wallId)
    {
        var panels = await db.WallPanels
            .Where(p => p.WallId == wallId)
            .Select(p => new { p.Id, p.Col, p.Row })
            .ToListAsync();
        return panels.ToDictionary(p => p.Id, p => (p.Col, p.Row));
    }

    /// <summary>
    /// Whether a hold sits on a panel re-photographed in this update. A null panel id means the hold is on
    /// the legacy centre photo, which the centre (0,0) — always re-shot — subsumes, so it counts as updated.
    /// </summary>
    private static bool IsOnUpdatedPanel(
        Guid? panelId,
        IReadOnlyDictionary<Guid, (int Col, int Row)> panelPositions,
        IReadOnlySet<(int Col, int Row)> updatedPositions)
    {
        if (panelId is not { } id)
        {
            return updatedPositions.Contains((0, 0));
        }

        return panelPositions.TryGetValue(id, out var pos) && updatedPositions.Contains(pos);
    }
}
