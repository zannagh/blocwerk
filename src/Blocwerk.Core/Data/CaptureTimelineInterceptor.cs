// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Runtime.CompilerServices;
using Blocwerk.Core.Capture;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Blocwerk.Core.Data;

/// <summary>
/// Keeps captures' stage timelines (<see cref="CaptureTimeline"/>). The changes are read before a save (and recomputed by a
/// retried one, so nothing is recorded twice) and merged onto the STORED timeline only after the save committed, with a
/// conditional write that reads again when another writer got there first: several workers save the same capture, and
/// writing the timeline the context loaded would drop what another one recorded meanwhile. Added by
/// <see cref="BlocwerkDbContext"/> itself, so every context has it whatever factory built it.
/// </summary>
public sealed class CaptureTimelineInterceptor : SaveChangesInterceptor
{
    private const int MaxAttempts = 20;

    private readonly ConditionalWeakTable<DbContext, List<CaptureTimelineChange>> pending = new();

    private CaptureTimelineInterceptor()
    {
    }

    /// <summary>The one instance (stateless apart from the per-context pending changes).</summary>
    public static CaptureTimelineInterceptor Instance { get; } = new();

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Collect(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Collect(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        foreach (var change in Take(eventData.Context))
        {
            ApplyAsync((BlocwerkDbContext)eventData.Context!, change, sync: true, CancellationToken.None).GetAwaiter().GetResult();
        }

        return base.SavedChanges(eventData, result);
    }

    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        foreach (var change in Take(eventData.Context))
        {
            await ApplyAsync((BlocwerkDbContext)eventData.Context!, change, sync: false, CancellationToken.None);
        }

        return await base.SavedChangesAsync(eventData, result, cancellationToken);
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData)
    {
        Take(eventData.Context);
        base.SaveChangesFailed(eventData);
    }

    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        Take(eventData.Context);
        return base.SaveChangesFailedAsync(eventData, cancellationToken);
    }

    /// <summary>Merges one change onto the stored timeline (conditional on the stored value; read again on a conflict).</summary>
    private static async Task ApplyAsync(BlocwerkDbContext db, CaptureTimelineChange change, bool sync, CancellationToken ct)
    {
        try
        {
            await MergeAsync(db, change, sync, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The save itself committed; a timeline that could not be merged must not turn it into a failure.
        }
    }

    private static async Task MergeAsync(BlocwerkDbContext db, CaptureTimelineChange change, bool sync, CancellationToken ct)
    {
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var rows = db.WallCaptures.AsNoTracking().Where(c => c.Id == change.CaptureId).Select(c => new { c.TimelineJson });
            var row = sync ? rows.FirstOrDefault() : await rows.FirstOrDefaultAsync(ct);
            if (row is null)
            {
                return;
            }

            var read = row.TimelineJson;
            var entries = CaptureTimeline.Parse(read);
            if (!CaptureTimeline.Apply(entries, change))
            {
                return;
            }

            var json = CaptureTimeline.ToJson(entries);
            var target = db.WallCaptures.Where(c => c.Id == change.CaptureId && c.TimelineJson == read);
            var written = sync
                ? target.ExecuteUpdate(s => s.SetProperty(c => c.TimelineJson, json))
                : await target.ExecuteUpdateAsync(s => s.SetProperty(c => c.TimelineJson, json), ct);
            if (written == 1)
            {
                return;
            }
        }
    }

    private void Collect(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        // Replaces what an earlier (failed) attempt of the same save collected.
        pending.AddOrUpdate(context, CaptureTimeline.Changes(context.ChangeTracker, DateTimeOffset.UtcNow));
    }

    private List<CaptureTimelineChange> Take(DbContext? context)
    {
        if (context is null || !pending.TryGetValue(context, out var changes))
        {
            return [];
        }

        pending.Remove(context);
        return changes;
    }
}
