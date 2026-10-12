// <copyright file="WallBigUpdateService.LookOver.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
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
    /// <summary>Gets or sets what works the findings out of a promote's facts (replaced in tests to make it fail).</summary>
    internal Func<LookOverPromoteFacts, List<WallLookOverFinding>> LookOverBuilder { get; set; } = LookOverFindingBuilder.Build;

    /// <summary>
    /// Retires what was open on the re-photographed panels, adds this promote's findings and flags the wall when there
    /// are any. Runs last, on the promote's own context before it saves, so it reads what the promote did from the
    /// change tracker and commits with it. Neither half can fail the promote, and the first does not depend on the
    /// second: a finding about a hold that just became history must never stay answerable.
    /// </summary>
    private async Task RecordLookOverAsync(BlocwerkDbContext db, Wall wall, int newGen, LookOverPromoteInputs inputs)
    {
        await SupersedeLookOverAsync(db, wall.Id, inputs.ReplacedPanelIds);
        try
        {
            var session = await WallUpdateSessions.FindOpenAsync(db, wall.Id);
            var cards = session is null
                ? []
                : await db.WallUpdateExceptions.AsNoTracking().Where(e => e.SessionId == session.Id).ToListAsync();
            var unaligned = session?.UnalignedCarryPanelIds.ToHashSet() ?? [];
            var findings = LookOverBuilder(ReadPromoteFacts(db, wall.Id, newGen, cards, unaligned, inputs));
            if (findings.Count == 0)
            {
                return;
            }

            foreach (var finding in findings)
            {
                (finding.WallId, finding.SessionId, finding.Generation) = (wall.Id, session?.Id, newGen);
            }

            // Nothing below can throw: the rows and the flag are saved by the promote, together or not at all.
            db.WallLookOverFindings.AddRange(findings);
            wall.LookOverPendingSince ??= DateTimeOffset.UtcNow;
            wall.LookOverGeneration = newGen;
            logger.LogInformation("The update of wall {WallId} to generation {Generation} left {Count} findings for the look-over", wall.Id, newGen, findings.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "The look-over findings of the update of wall {WallId} could not be worked out; the update is applied without them.", wall.Id);
        }
    }

    /// <summary>
    /// A panel that was photographed again has findings of its own now; what was open on its old photo is history, and
    /// so is the hold it was about.
    /// </summary>
    private async Task SupersedeLookOverAsync(BlocwerkDbContext db, Guid wallId, IReadOnlyCollection<Guid> replacedPanelIds)
    {
        try
        {
            var stale = await db.WallLookOverFindings
                .Where(f => f.WallId == wallId && f.Status == LookOverFindingStatus.Open && f.PanelId != null && replacedPanelIds.Contains(f.PanelId.Value))
                .ToListAsync();
            stale.ForEach(f => f.Status = LookOverFindingStatus.Superseded);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The page still refuses to change a hold that is no longer live, so a finding left open here is harmless.
            logger.LogError(ex, "The open look-over findings of wall {WallId} could not be retired; the update is applied all the same.", wallId);
        }
    }

    /// <summary>What the promote did, read from its change tracker: nothing of it is saved yet.</summary>
    private static LookOverPromoteFacts ReadPromoteFacts(
        BlocwerkDbContext db, Guid wallId, int newGen, IReadOnlyList<WallUpdateException> cards, HashSet<Guid> unaligned, LookOverPromoteInputs inputs)
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
            live,
            inputs.StagedHolds,
            inputs.Moves.NotFound ?? new Dictionary<Guid, HoldMoveOutcome>(),
            boulders,
            inputs.PhotoSizes,
            inputs.CardMarked,
            unaligned);
    }
}
