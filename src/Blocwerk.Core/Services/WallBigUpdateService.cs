using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
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
    private readonly IChangeJournal? changeJournal;
    private readonly IHoldEnrichmentService? holdEnrichment;
    private readonly IHoldOutlineService? outlineService;

    public WallBigUpdateService(
        IDbContextFactory<BlocwerkDbContext> dbContextFactory,
        ICurrentUserService currentUserService,
        IHoldDetectionService holdDetectionService,
        IHoldOverlapMatcher overlapMatcher,
        ILogger<WallBigUpdateService> logger,
        IChangeJournal? changeJournal = null,
        IHoldEnrichmentService? holdEnrichment = null,
        IHoldOutlineService? outlineService = null)
    {
        this.dbContextFactory = dbContextFactory;
        this.currentUserService = currentUserService;
        this.holdDetectionService = holdDetectionService;
        this.overlapMatcher = overlapMatcher;
        this.logger = logger;
        this.changeJournal = changeJournal;
        this.holdEnrichment = holdEnrichment;
        this.outlineService = outlineService;
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

        // A panel the matcher could not align is recorded on the session: the banner survives a resume and
        // the promote flags that panel's blind carries.
        await PersistAlignmentFailuresAsync(db, wallId, session);

        // Now the matcher's outcome is known: suggest "this hold moved" pairs among what it could not
        // pair positionally. Computed once per session and persisted, so a resume shows the same list.
        await EnsureRelocationProposalsAsync(db, wall, session);
        logger.LogInformation(
            "Big update resumed on wall {WallId} by {UserId}: {Carry} carryover, {Panels} neighbour panels",
            wallId, user.Id, session.Carryover.Count, session.Neighbours.Count);
        return session;
    }

    /// <summary>
    /// Runs the two matcher passes over the already-persisted staged panels and holds: the old live
    /// holds against the staged centre (carryover), and every non-centre staged panel against the
    /// staged centre (overlap). The sole caller is <see cref="ResumeAsync"/>, which runs it after
    /// <see cref="StageAsync"/> has persisted (and the user has corrected) the staged detections, so the
    /// session always reflects the current DB state. No detection, no mutation.
    /// </summary>
    private async Task<BigUpdateSession> BuildSessionAsync(
        BlocwerkDbContext db, Wall wall, Guid centerPanelId, int stagedGen)
    {
        // Panel-scoped carryover: match only the OLD holds that live on a re-photographed panel against
        // the new centre. A centre-only update therefore never pulls (and never warp-carries) the holds
        // on a non-updated panel onto the centre — they are left entirely alone. Null-panel legacy holds
        // count as centre, which is always updated.
        var updatedPositions = await LoadUpdatedPositionsAsync(db, wall.Id, stagedGen);
        var panelPositions = await LoadPanelPositionsAsync(db, wall.Id);
        var oldHolds = (await db.Holds
                .Where(h => h.WallId == wall.Id && h.Generation == wall.CurrentGeneration)
                .ToListAsync())
            .Where(h => IsOnUpdatedPanel(h.WallPanelId, panelPositions, updatedPositions))
            .ToList();

        // Keep crash-mat / floor false holds out of the matcher entirely so they are never offered as a
        // carryover proposal — mirrors the drop the promote carry applies, so the review matches the commit.
        oldHolds = await FilterCarriedMatFalseHoldsAsync(db, oldHolds);

        // Per-panel carryover: an old hold's twin is a fresh detection ON ITS OWN panel, never the centre.
        // Group the carried olds by grid position so the centre matches the staged centre and each updated
        // neighbour matches its own staged detections below (mirrors how the centre already works).
        var oldByPosition = GroupOldHoldsByPosition(oldHolds, panelPositions);
        var centreOldHolds = oldByPosition.GetValueOrDefault((0, 0)) ?? new List<Hold>();

        // Every live panel photo by id, so a neighbour's OLD panel image is available as the left side of
        // its own carryover match (the staged photo is the right side). Read before any promote mutates it.
        var oldPanelPhotosById = (await db.WallPanels
                .Where(p => p.WallId == wall.Id && p.Photo != null)
                .Select(p => new { p.Id, p.Photo })
                .ToListAsync())
            .ToDictionary(p => p.Id, p => p.Photo!);

        var centerHolds = await db.Holds
            .Where(h => h.WallPanelId == centerPanelId && h.Generation == stagedGen)
            .ToListAsync();
        var centerImage = await db.WallPanels
            .Where(p => p.Id == centerPanelId)
            .Select(p => p.StagedPhoto)
            .FirstAsync()
            ?? throw new InvalidOperationException("Centre panel has no staged photo.");

        var (oldMatcher, oldIndex) = BuildMatcherHolds(centreOldHolds);
        var (centerMatcher, centerIndex) = BuildMatcherHolds(centerHolds);

        var carryover = new List<CarryoverProposal>();
        var removedCandidates = new List<Guid>();
        var newCenter = new List<Guid>();
        var carriedWarp = new Dictionary<Guid, HoldPositionNorm>();
        var carriedShapes = new Dictionary<Guid, IReadOnlyList<HoldPositionNorm>>();
        var autoMatchStatus = AutoMatchStatus.Ok;
        string? autoMatchMessage = null;

        // Staged panels whose photo could not be lined up with the previous one: their old holds carry at
        // their OLD coordinates. Surfaced to the review (banner) and persisted by ResumeAsync — never silent.
        var unalignedCarry = new HashSet<Guid>();
        var recorded = await RecordedUnalignedAsync(db, wall.Id);
        var stagedCentre = new OverlapSeedSide(centerPanelId, true, centerImage, centerHolds);
        var centreSeed = await CentreCarrySeedAsync(
            db, wall, centreOldHolds, oldPanelPhotosById, panelPositions, stagedCentre);
        try
        {
            var carry = MatchUnlessUnaligned(
                recorded.Carry.Contains(centerPanelId),
                wall.Photo!, oldMatcher, centerImage, centerMatcher, HoldOverlapDirection.Right, logger, centreSeed);

            // Suggestion only: pre-fill the NewHoldId mapping the review layer will offer. The matcher
            // never asserts a hold has changed — "changed" is a purely manual decision in the UI, so
            // no Moved/Changed flag is carried out of here. Old holds with no proposal stay carried by
            // default (the review layer seeds carry-all), so an empty list is a valid, working session.
            CollectCarryover(carry, oldIndex, centerIndex, carryover, removedCandidates, carriedWarp, carriedShapes);

            // The centre's unmatched staged detections are the genuinely-new-centre holds. (A neighbour's
            // unmatched-new detections are simply kept by the keep-all-except-removed neighbour contract.)
            newCenter.AddRange(carry.UnmatchedRight.Select(i => centerIndex[i]));
        }
        catch (Exception ex)
        {
            // Fail soft: never silently seed nothing. Record WHY so the review layer can show a banner
            // ("map changed holds manually") instead of a mute 0/0/0/0. The session still returns the
            // full old-hold data below, so carry-all works with zero proposals.
            autoMatchStatus = ClassifyAutoMatchFailure(ex);
            autoMatchMessage = ex.Message;
            unalignedCarry.Add(centerPanelId);

            // None of them was re-found: count them as such (as a failed neighbour does), so the review's
            // "couldn't re-find" queue walks every one instead of reading as zero.
            removedCandidates.AddRange(centreOldHolds.Select(h => h.Id));
            logger.LogWarning(
                ex,
                "Carryover alignment {Status} for the centre panel on wall {WallId}: {Reason}. Its {Count} old holds are carried at their old positions, unconfirmed",
                autoMatchStatus, wall.Id, ex.Message, centreOldHolds.Count);
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
            var stagedNeighbour = new OverlapSeedSide(panel.Id, true, panel.StagedPhoto!, neighbourHolds);
            var oldNeighbourHolds = oldByPosition.GetValueOrDefault((panel.Col, panel.Row));
            var carrySeed = await NeighbourCarrySeedAsync(db, wall, oldNeighbourHolds, oldPanelPhotosById, stagedNeighbour);
            var overlapSeed = await OverlapSeedLoader.LoadAsync(db, wall, stagedCentre, stagedNeighbour, logger);

            // Same-panel carryover: match THIS neighbour's OLD holds against its OWN fresh staged
            // detections, so a co-updated neighbour's holds twin-match on their own panel exactly as the
            // centre does — a matched old promotes its twin in place (no clone), only a truly unmatched old
            // clones. Distinct from the cross-PANEL overlap match below, which links identity across seams.
            var aligned = MatchNeighbourCarryover(
                oldNeighbourHolds,
                oldPanelPhotosById, panel.StagedPhoto!, neighbourMatcher, neighbourIndex,
                carryover, removedCandidates, carriedWarp, carriedShapes, (wall.Id, panel.Col, panel.Row), carrySeed,
                recorded.Carry.Contains(panel.Id));
            if (!aligned)
            {
                unalignedCarry.Add(panel.Id);
            }

            var proposals = new List<OverlapProposalDto>();
            var overlapFailed = false;
            try
            {
                var result = MatchUnlessUnaligned(
                    recorded.Overlap.Contains(panel.Id),
                    centerImage, centerMatcher, panel.StagedPhoto!, neighbourMatcher, direction, null, overlapSeed);
                foreach (var p in result.Proposals)
                {
                    // NeighborPanelId is the panel HoldAId belongs to — here the staged CENTRE panel,
                    // NOT this non-centre panel. HoldAId is a staged centre hold (centerIndex) and the
                    // stepper draws the "existing neighbour" (left) image + its holds from NeighborPanelId,
                    // so it must resolve to the centre. Passing panel.Id made the left request this
                    // staged-only panel's committed /photo, which 404s, and overlaid a foreign hold.
                    proposals.Add(new OverlapProposalDto(
                        centerPanelId, centerIndex[p.LeftHoldId], neighbourIndex[p.RightHoldId],
                        p.Confidence, p.Moved, p.ResidualPx));
                }
            }
            catch (Exception ex)
            {
                overlapFailed = true;
                logger.LogWarning(
                    ex,
                    "Overlap alignment failed for panel {PanelId} ({Col},{Row}) vs the staged centre on wall {WallId}: {Reason}. No link proposals for it",
                    panel.Id, panel.Col, panel.Row, wall.Id, ex.Message);
            }

            neighbours.Add(new NeighbourOverlap(panel.Id, panel.Col, panel.Row, proposals, overlapFailed));
        }

        return new BigUpdateSession(
            wall.Id, centerPanelId, carryover, removedCandidates, newCenter, neighbours,
            autoMatchStatus, autoMatchMessage, carriedWarp, carriedShapes,
            await BuildCarriedPanelsAsync(db, wall.Id, stagedGen, oldByPosition, unalignedCarry),
            oldHolds.Select(h => h.Id).ToList());
    }

    /// <summary>
    /// Buckets the carried old holds by their grid position (null panel = legacy centre = (0,0)), so the
    /// centre and each updated neighbour each match their own holds against their own staged detections.
    /// </summary>
    private static Dictionary<(int Col, int Row), List<Hold>> GroupOldHoldsByPosition(
        IReadOnlyList<Hold> oldHolds,
        IReadOnlyDictionary<Guid, (int Col, int Row)> panelPositions)
    {
        var byPosition = new Dictionary<(int Col, int Row), List<Hold>>();
        foreach (var hold in oldHolds)
        {
            var pos = hold.WallPanelId is { } id && panelPositions.TryGetValue(id, out var p) ? p : (0, 0);
            if (!byPosition.TryGetValue(pos, out var list))
            {
                list = new List<Hold>();
                byPosition[pos] = list;
            }

            list.Add(hold);
        }

        return byPosition;
    }

    /// <summary>
    /// Translates one matcher pass into the session's carryover suggestions: the matched old→staged
    /// proposals, the unmatched-old removal candidates, and the warp-predicted new-image position/outline
    /// for every old hold the field could warp. Shared by the centre and each neighbour panel so both
    /// produce identical carryover semantics. <paramref name="oldIndex"/>/<paramref name="stagedIndex"/>
    /// map matcher ids back to real hold ids (the order <see cref="BuildMatcherHolds"/> produced).
    /// </summary>
    private static void CollectCarryover(
        HoldOverlapResult carry,
        Guid[] oldIndex,
        Guid[] stagedIndex,
        List<CarryoverProposal> carryover,
        List<Guid> removedCandidates,
        Dictionary<Guid, HoldPositionNorm> carriedWarp,
        Dictionary<Guid, IReadOnlyList<HoldPositionNorm>> carriedShapes)
    {
        foreach (var p in carry.Proposals)
        {
            carryover.Add(new CarryoverProposal(
                oldIndex[p.LeftHoldId], stagedIndex[p.RightHoldId], p.Confidence, p.ResidualPx));
        }

        removedCandidates.AddRange(carry.UnmatchedLeft.Select(i => oldIndex[i]));

        // Warp-carry: record the matcher's warp-predicted new-image position for EVERY old hold the field
        // could predict — matched and unmatched alike (indices align 1:1 with oldIndex) — exactly as the
        // shapes pass below does. Promote consults this dictionary ONLY in the clone branch (an old hold
        // whose decision names no staged twin), so an entry for a matched hold is inert; what it buys is
        // that the dictionary no longer depends on WHICH holds this particular matcher pass happened to
        // match.
        //
        // That dependence was a real hazard. The matcher runs once before the pre-match touch-up on an
        // uninterrupted run, and again over the touched-up staged geometry when an update is RESUMED, so
        // the two passes legitimately produce different proposal sets. Recording only the unmatched ones
        // meant an old hold the user blind-carried (no proposal, warp position recorded, decision
        // persisted with no twin) could be MATCHED by the re-run, lose its warp entry, and then be cloned
        // at its stale gen-N coordinates — the decision says "no twin", so the twin is never used either.
        // Recording every prediction removes the coupling: the persisted decision decides what happens,
        // and the warp position is simply available whenever a clone needs one.
        var warped = carry.WarpedLeftPositions;
        if (warped is not null)
        {
            for (var i = 0; i < oldIndex.Length; i++)
            {
                if (i >= warped.Count || warped[i] is not { } pos)
                {
                    continue;
                }

                carriedWarp[oldIndex[i]] = new HoldPositionNorm(pos.X, pos.Y);
            }
        }

        // Warp-carry (shapes): for EVERY old hold that had a custom outline the matcher could warp (matched
        // AND unmatched — a hand-drawn polygon must carry regardless, since a detection has none), record
        // the warped new-image polygon. Promote sets the successor's ShapePoints to it.
        var warpedShapes = carry.WarpedLeftShapes;
        if (warpedShapes is not null)
        {
            for (var i = 0; i < oldIndex.Length; i++)
            {
                if (i >= warpedShapes.Count || warpedShapes[i] is not { } shape)
                {
                    continue;
                }

                carriedShapes[oldIndex[i]] = shape.Select(v => new HoldPositionNorm(v.X, v.Y)).ToList();
            }
        }
    }
}
