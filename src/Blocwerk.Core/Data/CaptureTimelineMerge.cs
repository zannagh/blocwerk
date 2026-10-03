// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Runtime.CompilerServices;
using Blocwerk.Core.Capture;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Data;

/// <summary>
/// Merging collected <see cref="CaptureTimelineChange"/>s onto the stored timelines: a conditional write that reads again
/// when another writer got there first. Changes saved inside a caller's transaction wait here until it commits
/// (<see cref="CaptureTimelineTransactionInterceptor"/>) and are then merged by a context of their own, outside it.
/// </summary>
internal static class CaptureTimelineMerge
{
    private const int MaxAttempts = 20;

    private static readonly ConditionalWeakTable<DbContext, List<CaptureTimelineChange>> Deferred = new();

    /// <summary>Keeps <paramref name="changes"/> until the context's transaction commits (dropped on a rollback).</summary>
    public static void Defer(DbContext context, List<CaptureTimelineChange> changes)
    {
        lock (Deferred)
        {
            var list = Deferred.GetOrCreateValue(context);
            list.AddRange(changes);
        }
    }

    /// <summary>Takes (and forgets) what waits for the context's transaction.</summary>
    public static List<CaptureTimelineChange> TakeDeferred(DbContext? context)
    {
        if (context is null)
        {
            return [];
        }

        lock (Deferred)
        {
            if (!Deferred.TryGetValue(context, out var list))
            {
                return [];
            }

            Deferred.Remove(context);
            return list;
        }
    }

    /// <summary>
    /// Merges every change, on a fresh context over the same options when <paramref name="fresh"/> (the caller's
    /// transaction just committed, but its context still holds it). Never throws: the save itself committed; a timeline
    /// that cannot be merged is logged, not turned into a failure.
    /// </summary>
    public static async Task MergeAllAsync(DbContext context, List<CaptureTimelineChange> changes, bool sync, bool fresh)
    {
        if (changes.Count == 0)
        {
            return;
        }

        var logger = Logger(context);
        BlocwerkDbContext? own = null;
        try
        {
            own = fresh ? new BlocwerkDbContext((DbContextOptions<BlocwerkDbContext>)context.GetService<IDbContextOptions>()) : null;
            var db = own ?? (BlocwerkDbContext)context;
            foreach (var change in changes)
            {
                if (!await MergeAsync(db, change, sync))
                {
                    logger.LogWarning(
                        "Capture {CaptureId}: its stage timeline kept changing; this save's change was not merged after {Attempts} tries",
                        change.CaptureId, MaxAttempts);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not merge {Count} capture timeline change(s)", changes.Count);
        }
        finally
        {
            if (own is not null)
            {
                await own.DisposeAsync();
            }
        }
    }

    /// <summary>One change; false when other writers kept winning for <see cref="MaxAttempts"/> tries.</summary>
    private static async Task<bool> MergeAsync(BlocwerkDbContext db, CaptureTimelineChange change, bool sync)
    {
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var rows = db.WallCaptures.AsNoTracking().Where(c => c.Id == change.CaptureId).Select(c => new { c.TimelineJson });
            var row = sync ? rows.FirstOrDefault() : await rows.FirstOrDefaultAsync();
            if (row is null)
            {
                return true;
            }

            var read = row.TimelineJson;
            var entries = CaptureTimeline.Parse(read);
            if (!CaptureTimeline.Apply(entries, change))
            {
                return true;
            }

            var json = CaptureTimeline.ToJson(entries);
            var target = db.WallCaptures.Where(c => c.Id == change.CaptureId && c.TimelineJson == read);
            var written = sync
                ? target.ExecuteUpdate(s => s.SetProperty(c => c.TimelineJson, json))
                : await target.ExecuteUpdateAsync(s => s.SetProperty(c => c.TimelineJson, json));
            if (written == 1)
            {
                return true;
            }
        }

        return false;
    }

    private static ILogger Logger(DbContext context)
    {
        try
        {
            return context.GetService<ILoggerFactory>()?.CreateLogger(typeof(CaptureTimelineInterceptor)) ?? NullLogger.Instance;
        }
        catch (InvalidOperationException)
        {
            return NullLogger.Instance;
        }
    }
}
