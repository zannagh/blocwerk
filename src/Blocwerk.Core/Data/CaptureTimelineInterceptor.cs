// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Runtime.CompilerServices;
using Blocwerk.Core.Capture;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Blocwerk.Core.Data;

/// <summary>
/// Keeps captures' stage timelines (<see cref="CaptureTimeline"/>). The changes are read before a save (and recomputed by a
/// retried one, so nothing is recorded twice) and merged onto the STORED timeline only once the write is committed
/// (<see cref="CaptureTimelineMerge"/>: a conditional write that reads again when another writer got there first; several
/// workers save the same capture, and writing the timeline the context loaded would drop what another one recorded).
/// A save outside a transaction is committed when it returns, so it merges right away; a save inside the caller's
/// transaction merges when that transaction commits (<see cref="CaptureTimelineTransactionInterceptor"/>), and not at all
/// when it rolls back. Added by <see cref="BlocwerkDbContext"/> itself, so every context has it whatever factory built it.
/// </summary>
public sealed class CaptureTimelineInterceptor : SaveChangesInterceptor
{
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
        Merge(eventData.Context, sync: true).GetAwaiter().GetResult();
        return base.SavedChanges(eventData, result);
    }

    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        await Merge(eventData.Context, sync: false);
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

    /// <summary>
    /// Merges what the save changed, or, inside a caller's transaction, keeps it for when that commits: the merge must
    /// neither run in a transaction that may still roll back nor make it longer.
    /// </summary>
    private Task Merge(DbContext? context, bool sync)
    {
        var changes = Take(context);
        if (context is null || changes.Count == 0)
        {
            return Task.CompletedTask;
        }

        if (context.Database.CurrentTransaction is not null)
        {
            CaptureTimelineMerge.Defer(context, changes);
            return Task.CompletedTask;
        }

        return CaptureTimelineMerge.MergeAllAsync(context, changes, sync, fresh: false);
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
