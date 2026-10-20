// <copyright file="WallEnhancementService.View.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Enhancement;

/// <summary>Building the card's view of the current generation's enhancement.</summary>
public sealed partial class WallEnhancementService
{
    private async Task<WallEnhancementView> BuildViewAsync(BlocwerkDbContext db, Guid wallId, CancellationToken ct)
    {
        var generation = await GenerationAsync(db, wallId, ct);

        // Few rows per wall; the finalized dates are compared in memory (SQLite cannot order a DateTimeOffset).
        var runs = await db.WallEnhancementRuns.AsNoTracking().Where(r => r.WallId == wallId).ToListAsync(ct);
        var run = runs.FirstOrDefault(r => r.Generation == generation);
        var previous = runs.Where(r => r.Generation != generation && r.FinalizedAt is not null).Max(r => r.FinalizedAt);
        var blocked = run is null or { Status: WallEnhancementStatus.Failed } ? await BlockedReasonAsync(db, wallId, ct) : null;
        if (run is null)
        {
            var fresh = runner.Job.Steps.Select(s => new WallEnhancementStepView(s.Key, s.Title, null, string.Empty)).ToList();
            return new WallEnhancementView(
                WallEnhancementState.NotStarted, generation, null, fresh, null, [], null, null, null, null, blocked, null, false, previous);
        }

        var records = WallEnhancementStepRecord.Parse(run.StepsJson);
        var steps = runner.Job.Steps
            .Select(s => records.FirstOrDefault(r => r.Key == s.Key) is { } r
                ? new WallEnhancementStepView(s.Key, s.Title, r.Outcome, r.Summary)
                : new WallEnhancementStepView(s.Key, s.Title, null, string.Empty))
            .ToList();
        var current = run.CurrentStep is { } key ? steps.FirstOrDefault(s => s.Key == key) : null;
        var names = await NamesAsync(db, [run.CreatedByUserId, run.FinalizedByUserId], ct);
        var running = run.Status == WallEnhancementStatus.Running;
        var progress = running ? runner.Job.ProgressOf(run.Id) : null;
        return new WallEnhancementView(
            StateOf(run.Status),
            generation,
            run.Id,
            steps,
            current,
            await CountsAsync(db, run.Id, ct),
            run.CreatedAt,
            names.GetValueOrDefault(run.CreatedByUserId),
            run.FinalizedAt,
            run.FinalizedByUserId is { } by ? names.GetValueOrDefault(by) : null,
            blocked,
            run.Error,
            running && editPresence?.IsBeingEdited(wallId) == true,
            previous,
            progress?.Progress,
            progress?.ExpectedEnd,
            running && WallEnhancementGuard.IsStale(run, DateTimeOffset.UtcNow));
    }

    private static WallEnhancementState StateOf(WallEnhancementStatus status) => status switch
    {
        WallEnhancementStatus.Running => WallEnhancementState.Running,
        WallEnhancementStatus.Review => WallEnhancementState.Review,
        WallEnhancementStatus.Finalized => WallEnhancementState.Finalized,
        _ => WallEnhancementState.Failed,
    };

    private static async Task<List<WallEnhancementKindCount>> CountsAsync(BlocwerkDbContext db, Guid runId, CancellationToken ct)
    {
        var rows = await db.WallEnhancementItems.AsNoTracking()
            .Where(i => i.RunId == runId)
            .GroupBy(i => new { i.Kind, i.Decision })
            .Select(g => new { g.Key.Kind, g.Key.Decision, Count = g.Count() })
            .ToListAsync(ct);
        int Count(WallEnhancementItemKind kind, WallEnhancementDecision decision) =>
            rows.Where(r => r.Kind == kind && r.Decision == decision).Sum(r => r.Count);
        return WallEnhancementKinds.All
            .Select(k => new WallEnhancementKindCount(
                k,
                Count(k, WallEnhancementDecision.Pending),
                Count(k, WallEnhancementDecision.Accepted),
                Count(k, WallEnhancementDecision.Rejected),
                Count(k, WallEnhancementDecision.Skipped)))
            .Where(c => c.Total > 0)
            .ToList();
    }

    private static async Task<Dictionary<Guid, string>> NamesAsync(BlocwerkDbContext db, IEnumerable<Guid?> ids, CancellationToken ct)
    {
        var wanted = ids.OfType<Guid>().Distinct().ToList();
        var users = await db.Users.IgnoreQueryFilters().AsNoTracking()
            .Where(u => wanted.Contains(u.Id))
            .Select(u => new { u.Id, u.CustomDisplayName, u.DisplayName })
            .ToListAsync(ct);
        return users.ToDictionary(u => u.Id, u => string.IsNullOrWhiteSpace(u.CustomDisplayName) ? u.DisplayName : u.CustomDisplayName!);
    }
}
