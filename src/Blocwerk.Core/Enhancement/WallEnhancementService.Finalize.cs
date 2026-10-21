// <copyright file="WallEnhancementService.Finalize.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Enhancement;

/// <summary>Finalizing the review, and listing its items.</summary>
public sealed partial class WallEnhancementService
{
    private static readonly JsonSerializerOptions OutcomeJson = new(JsonSerializerDefaults.Web);

    /// <inheritdoc/>
    public async Task<WallEnhancementView> FinalizeAsync(Guid wallId, bool withoutFailedSteps = false, CancellationToken ct = default)
    {
        var (db, userId) = await OpenForAdminAsync(wallId, ct);
        await using (db)
        {
            var run = await ReviewRunAsync(db, wallId, ct);
            if (WallEnhancementRunner.IsLive(run.Id))
            {
                throw new UserFacingException(WallEnhancementGuard.StillStopping);
            }

            var counts = await CountsAsync(db, run.Id, ct);
            var pending = counts.Sum(c => c.Pending);
            if (pending > 0)
            {
                throw new UserFacingException($"{WallEnhancementKinds.Count(pending, "change still waits", "changes still wait")} for an answer.");
            }

            var failed = WallEnhancementStepRecord.Parse(run.StepsJson).Where(s => s.Outcome == WallEnhancementStepOutcomeKind.Failed).ToList();
            if (failed.Count > 0 && !withoutFailedSteps)
            {
                throw new UserFacingException(
                    $"These steps failed: {string.Join(", ", failed.Select(f => f.Title))}. Retry them, or finalize without them.");
            }

            run.Status = WallEnhancementStatus.Finalized;
            run.FinalizedAt = DateTimeOffset.UtcNow;
            run.FinalizedByUserId = userId;
            run.UpdatedAt = run.FinalizedAt.Value;
            run.OutcomeJson = JsonSerializer.Serialize(
                new
                {
                    kinds = counts.Select(c => new { kind = c.Kind.ToString(), kept = c.Accepted, undone = c.Rejected, skipped = c.Skipped }),
                    steps = WallEnhancementStepRecord.Parse(run.StepsJson)
                        .Select(s => new { s.Key, outcome = s.Outcome.ToString(), s.Summary, batches = s.BatchIds.Count }),
                    finalizedWithoutFailedSteps = failed.Select(f => f.Key),
                },
                OutcomeJson);
            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "Wall enhancement {RunId} of wall {WallId} generation {Generation} finalized by {UserId}: {Outcome}",
                run.Id, wallId, run.Generation, userId, run.OutcomeJson);
            return await BuildViewAsync(db, wallId, ct);
        }
    }

    /// <inheritdoc/>
    public async Task<WallEnhancementItemPage> ListItemsAsync(
        Guid wallId, WallEnhancementItemKind kind, bool pendingOnly, int skip, int take, CancellationToken ct = default)
    {
        var (db, _) = await OpenForAdminAsync(wallId, ct);
        await using (db)
        {
            var generation = await GenerationAsync(db, wallId, ct);
            var runId = await db.WallEnhancementRuns.Where(r => r.WallId == wallId && r.Generation == generation).Select(r => (Guid?)r.Id).FirstOrDefaultAsync(ct);
            if (runId is null)
            {
                return new WallEnhancementItemPage(0, []);
            }

            var items = await db.WallEnhancementItems.AsNoTracking()
                .Where(i => i.RunId == runId && i.Kind == kind && (!pendingOnly || i.Decision == WallEnhancementDecision.Pending))
                .ToListAsync(ct);

            // Pending first, then in the order the steps found them (in memory: SQLite cannot order a DateTimeOffset).
            var page = items.OrderBy(i => i.Decision != WallEnhancementDecision.Pending).ThenBy(i => i.CreatedAt).ThenBy(i => i.Id)
                .Skip(Math.Max(0, skip)).Take(Math.Clamp(take, 1, 100)).ToList();
            var holdIds = page.Select(i => i.HoldId).OfType<Guid>().ToList();
            var holds = await db.Holds.AsNoTracking()
                .Where(h => holdIds.Contains(h.Id))
                .Select(h => new { h.Id, h.Name, h.Color, h.X, h.Y })
                .ToDictionaryAsync(h => h.Id, ct);
            return new WallEnhancementItemPage(items.Count, page.Select(i =>
            {
                var h = i.HoldId is { } id ? holds.GetValueOrDefault(id) : null;
                return new WallEnhancementItemView(
                    i.Id, i.Kind, i.HoldId, i.OtherHoldId, i.ReferenceId, i.Detail, i.Decision, i.Note, h?.Name, h?.Color, h?.X, h?.Y);
            }).ToList());
        }
    }
}
