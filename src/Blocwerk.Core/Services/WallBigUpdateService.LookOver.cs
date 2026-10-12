// <copyright file="WallBigUpdateService.LookOver.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.HoldMoves;
using Blocwerk.Core.LookOver;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

/// <summary>
/// What a promote leaves for the wall look-over: the findings nobody answered are kept with the wall
/// (<see cref="WallLookOverFinding"/>) and the wall is flagged until a wall admin or moderator has looked it over.
/// Every promote comes through here, whoever started it (the confirm screen, the API, the step-by-step review).
/// </summary>
public partial class WallBigUpdateService
{
    /// <summary>
    /// Adds this promote's findings and flags the wall when there are any. Runs last, on the promote's own context
    /// before it saves, so it reads what the promote did from the change tracker and commits with it. It never fails
    /// the promote: an update that went through without its findings is better than one that did not go through.
    /// </summary>
    private async Task RecordLookOverAsync(BlocwerkDbContext db, Wall wall, int newGen, LookOverPromoteInputs inputs)
    {
        List<WallLookOverFinding> findings;
        Guid? sessionId;
        try
        {
            var session = await WallUpdateSessions.FindOpenAsync(db, wall.Id);
            sessionId = session?.Id;
            var cards = sessionId is { } id
                ? await db.WallUpdateExceptions.AsNoTracking().Where(e => e.SessionId == id).ToListAsync()
                : [];
            findings = LookOverFindingBuilder.Build(ReadPromoteFacts(db, wall.Id, newGen, cards, inputs));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "The look-over findings of the update of wall {WallId} could not be worked out; the update is applied without them.", wall.Id);
            return;
        }

        // A panel that was photographed again has findings of its own now; what was open on its old photo is history.
        var stale = await db.WallLookOverFindings
            .Where(f => f.WallId == wall.Id && f.Status == LookOverFindingStatus.Open && f.PanelId != null && inputs.ReplacedPanelIds.Contains(f.PanelId.Value))
            .ToListAsync();
        stale.ForEach(f => f.Status = LookOverFindingStatus.Superseded);
        if (findings.Count == 0)
        {
            return;
        }

        foreach (var finding in findings)
        {
            (finding.WallId, finding.SessionId, finding.Generation) = (wall.Id, sessionId, newGen);
        }

        db.WallLookOverFindings.AddRange(findings);
        wall.LookOverPendingSince = DateTimeOffset.UtcNow;
        wall.LookOverGeneration = newGen;
        wall.LookOverCompletedAt = null;
        wall.LookOverCompletedByUserId = null;
        logger.LogInformation("The update of wall {WallId} to generation {Generation} left {Count} findings for the look-over", wall.Id, newGen, findings.Count);
    }

    /// <summary>What the promote did, read from its change tracker: nothing of it is saved yet.</summary>
    private static LookOverPromoteFacts ReadPromoteFacts(
        BlocwerkDbContext db, Guid wallId, int newGen, IReadOnlyList<WallUpdateException> cards, LookOverPromoteInputs inputs)
    {
        var live = db.Holds.Local
            .Where(h => h.WallId == wallId && h.Generation == newGen && db.Entry(h).State != EntityState.Deleted)
            .GroupBy(h => h.Id)
            .ToDictionary(g => g.Key, g => g.First());
        var successors = db.HoldGenerationLinks.Local
            .Where(l => l is { OldHoldId: not null, NewHoldId: not null } && l.ToGeneration == newGen && db.Entry(l).State == EntityState.Added)
            .GroupBy(l => l.OldHoldId!.Value)
            .ToDictionary(g => g.Key, g => g.First().NewHoldId!.Value);
        var boulders = db.BoulderHolds.Local
            .Where(bh => db.Entry(bh).State == EntityState.Added)
            .GroupBy(bh => bh.HoldId)
            .ToDictionary(g => g.Key, g => g.Select(bh => bh.BoulderId).Distinct().Count());
        var carry = inputs.Confirmation.Carryover.GroupBy(d => d.OldHoldId).ToDictionary(g => g.Key, g => g.First());
        return new LookOverPromoteFacts(
            cards,
            carry,
            successors,
            inputs.OldHolds,
            live,
            inputs.StagedHolds,
            inputs.Moves.NotFound ?? new Dictionary<Guid, HoldMoveOutcome>(),
            boulders,
            inputs.PhotoSizes);
    }
}

/// <summary>What <see cref="WallBigUpdateService.PromoteAsync"/> hands to the look-over step.</summary>
/// <param name="Confirmation">The promoted decisions (accepted relocations folded in).</param>
/// <param name="Moves">The move plan the promote acted on.</param>
/// <param name="OldHolds">The old holds of the re-photographed panels.</param>
/// <param name="StagedHolds">Every staged hold of those panels.</param>
/// <param name="ReplacedPanelIds">The live panels the update replaces.</param>
/// <param name="PhotoSizes">The new photos' pixel sizes per staged panel.</param>
internal sealed record LookOverPromoteInputs(
    BigUpdateConfirmation Confirmation,
    HoldMovePlan Moves,
    IReadOnlyDictionary<Guid, Hold> OldHolds,
    IReadOnlyDictionary<Guid, Hold> StagedHolds,
    IReadOnlyCollection<Guid> ReplacedPanelIds,
    IReadOnlyDictionary<Guid, (int Width, int Height)> PhotoSizes);
