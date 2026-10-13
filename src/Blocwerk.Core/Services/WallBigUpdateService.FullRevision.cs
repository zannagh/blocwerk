// <copyright file="WallBigUpdateService.FullRevision.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

/// <summary>
/// The full revision of a wall that was reset completely (<see cref="WallUpdateSession.FullRevision"/>): no old hold is
/// matched, looked at or carried, every detection that passes the new-hold triage becomes a new hold, the overlaps
/// between the new panels are still linked, and every boulder of the wall is archived at the promote. The old holds
/// stay as the history of their generation, so the archived boulders keep showing on their old photo.
/// </summary>
public partial class WallBigUpdateService
{
    /// <summary>The refusal when a full revision does not come with a new photo for every panel.</summary>
    public const string FullRevisionNeedsEveryPanel =
        "Starting over needs a new photo for every panel of the wall, so no panel keeps holds from before the reset.";

    /// <summary>Whether the wall's open update is a full revision.</summary>
    private static async Task<bool> IsFullRevisionAsync(BlocwerkDbContext db, Guid wallId) =>
        (await WallUpdateSessions.FindOpenAsync(db, wallId))?.FullRevision == true;

    /// <summary>A full revision promotes no carry decision, whatever the session still records.</summary>
    private static async Task<BigUpdateConfirmation> WithoutCarryInFullRevisionAsync(
        BlocwerkDbContext db, Guid wallId, BigUpdateConfirmation confirmation) =>
        await IsFullRevisionAsync(db, wallId)
            ? confirmation with { Carryover = [], ReviewOldHoldIds = null, HandPlacedMergeOldIds = null, NotFoundAgain = null }
            : confirmation;

    /// <summary>
    /// Refuses a full revision that leaves a live panel out: that panel would keep its old holds next to a wall that
    /// starts over, with every boulder archived.
    /// </summary>
    private static async Task EnsureFullRevisionCoversWallAsync(
        BlocwerkDbContext db, Wall wall, IReadOnlyList<BigUpdatePhoto> photos)
    {
        var staged = photos.Select(p => (p.Col, p.Row)).ToHashSet();
        var positions = await LoadPanelPositionsAsync(db, wall.Id);
        var live = await LiveWallHolds.LoadPanelIdsAsync(db, wall.Id);
        if (live.Any(id => positions.TryGetValue(id, out var at) && !staged.Contains(at)))
        {
            throw new UserFacingException(FullRevisionNeedsEveryPanel);
        }
    }

    /// <summary>
    /// The matched session of a full revision: every staged centre detection is a new centre hold, each neighbour gets
    /// its overlap proposals against the centre, and the new-hold triage runs with nothing old to compare against.
    /// </summary>
    private async Task<BigUpdateSession> BuildFullRevisionSessionAsync(
        BlocwerkDbContext db, Wall wall, Guid centerPanelId, int stagedGen, bool use3DEvidence)
    {
        var centerHolds = await db.Holds
            .Where(h => h.WallPanelId == centerPanelId && h.Generation == stagedGen)
            .OrderBy(h => h.Id)
            .ToListAsync();
        var centerImage = await db.WallPanels.Where(p => p.Id == centerPanelId).Select(p => p.StagedPhoto).FirstAsync()
            ?? throw new InvalidOperationException("Centre panel has no staged photo.");
        var (centerMatcher, centerIndex) = BuildMatcherHolds(centerHolds);
        var recorded = await RecordedUnalignedAsync(db, wall.Id);
        var stagedCentre = new OverlapSeedSide(centerPanelId, true, centerImage, centerHolds);
        var neighbours = new List<NeighbourOverlap>();
        var stagedPanels = await db.WallPanels
            .Where(p => p.WallId == wall.Id && p.Generation == stagedGen && p.StagedPhoto != null && p.Id != centerPanelId)
            .OrderBy(p => p.Row).ThenBy(p => p.Col)
            .ToListAsync();
        foreach (var panel in stagedPanels)
        {
            var holds = await db.Holds
                .Where(h => h.WallPanelId == panel.Id && h.Generation == stagedGen)
                .OrderBy(h => h.Id)
                .ToListAsync();
            var (matcher, index) = BuildMatcherHolds(holds);
            var seed = await OverlapSeedLoader.LoadAsync(
                db, wall, stagedCentre, new OverlapSeedSide(panel.Id, true, panel.StagedPhoto!, holds), logger);
            var proposals = ProposeOverlapLinks(
                recorded.Overlap.Contains(panel.Id), centerImage, centerMatcher, centerIndex, panel.StagedPhoto!, matcher, index,
                DirectionFromNeighbor(0, 0, panel.Col, panel.Row), seed, (wall.Id, centerPanelId, panel.Id, panel.Col, panel.Row));
            neighbours.Add(new NeighbourOverlap(panel.Id, panel.Col, panel.Row, proposals ?? [], proposals is null));
        }

        var nothingOld = new RemovalInputs(
            new Dictionary<(int Col, int Row), List<Hold>>(), new HashSet<Guid>(), new Dictionary<Guid, HoldPositionNorm>());
        var triage = await SuggestNewDiscardsAsync(
            db, wall, stagedGen, [], [], new Dictionary<Guid, byte[]>(), null, neighbours, use3DEvidence, nothingOld);
        return new BigUpdateSession(
            wall.Id, centerPanelId, [], [], centerHolds.Select(h => h.Id).ToList(), neighbours,
            CarriedPanels: [],
            CarriedOldHoldIds: [],
            SuggestedNewDiscards: triage.Discards,
            SeenIn3DHoldIds: triage.SeenIn3D,
            Evidence3DModelId: triage.Evidence3DModelId,
            ConflictingNew: use3DEvidence ? triage.ConflictingNew : null);
    }

    /// <summary>
    /// The link proposals between the staged centre and one staged neighbour, each as (centre hold, neighbour hold); null
    /// when the two photos could not be lined up.
    /// </summary>
    private List<OverlapProposalDto>? ProposeOverlapLinks(
        bool recordedUnaligned,
        byte[] centerImage,
        IReadOnlyList<MatcherHold> centerMatcher,
        Guid[] centerIndex,
        byte[] neighbourImage,
        IReadOnlyList<MatcherHold> neighbourMatcher,
        Guid[] neighbourIndex,
        HoldOverlapDirection direction,
        HoldOverlapSeed? seed,
        (Guid WallId, Guid CentrePanelId, Guid PanelId, int Col, int Row) at)
    {
        try
        {
            var result = MatchUnlessUnaligned(
                recordedUnaligned, centerImage, centerMatcher, neighbourImage, neighbourMatcher, direction, null, seed);

            // NeighborPanelId is the panel HoldAId belongs to: the staged CENTRE panel, not this neighbour (the stepper
            // draws the left image and its holds from it).
            return result.Proposals
                .Select(p => new OverlapProposalDto(
                    at.CentrePanelId, centerIndex[p.LeftHoldId], neighbourIndex[p.RightHoldId], p.Confidence, p.Moved, p.ResidualPx))
                .ToList();
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Overlap alignment failed for panel {PanelId} ({Col},{Row}) vs the staged centre on wall {WallId}: {Reason}. No link proposals for it",
                at.PanelId, at.Col, at.Row, at.WallId, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Archives every boulder of the wall that is not archived yet: the holds they use are gone from the wall. Their
    /// memberships stay on the retained old holds, so each still shows on the photo of its generation.
    /// </summary>
    private async Task ArchiveBouldersForFullRevisionAsync(BlocwerkDbContext db, Guid wallId, int oldGen)
    {
        var boulders = await db.Boulders.Where(b => b.WallId == wallId && !b.IsArchived).ToListAsync();
        foreach (var boulder in boulders)
        {
            boulder.IsHistoric = true;
            boulder.IsArchived = true;
            boulder.NeedsReview = false;
        }

        logger.LogInformation(
            "Full revision of wall {WallId}: {Count} boulders of generation {Generation} and before archived", wallId, boulders.Count, oldGen);
    }
}
