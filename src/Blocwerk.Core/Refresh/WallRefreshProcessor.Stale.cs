// <copyright file="WallRefreshProcessor.Stale.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Refresh;

/// <summary>
/// Runs left waiting for the user (uploading, sort screen, confirm screen) for a day are discarded: their draft
/// photos, staged panel update and videos are released, exactly as "Discard this update" does. Nothing live changes.
/// Work in the full review is activity too: it writes the update session, not the run, so a run whose session was
/// written within the limit is kept.
/// </summary>
public sealed partial class WallRefreshProcessor
{
    /// <summary>How long a run may wait for the user before it is discarded.</summary>
    public static readonly TimeSpan IdleLimit = TimeSpan.FromHours(24);

    public async Task<int> DiscardStaleAsync(DateTimeOffset now, CancellationToken ct)
    {
        List<WallRefresh> stale;
        await using (var db = dbContextFactory.CreateDbContext())
        {
            // Few rows; filtered in memory because SQLite cannot compare a DateTimeOffset.
            stale = (await db.WallRefreshes.AsNoTracking()
                    .Where(r => r.Status == WallRefreshStatus.Uploading || r.Status == WallRefreshStatus.ReadyToStart
                                || r.Status == WallRefreshStatus.ReadyToApply)
                    .ToListAsync(ct))
                .Where(r => r.UpdatedAt < now - IdleLimit)
                .ToList();
        }

        var discarded = 0;
        foreach (var refresh in stale)
        {
            discarded += await DiscardIdleAsync(refresh, now, ct) ? 1 : 0;
        }

        return discarded;
    }

    private async Task<bool> DiscardIdleAsync(WallRefresh refresh, DateTimeOffset now, CancellationToken ct)
    {
        var gate = wallLocks.GetOrAdd(refresh.WallId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            // The user may have come back in the meantime.
            var current = await LoadAsync(refresh.Id, ct);
            if (current is null || current.Status != refresh.Status || current.UpdatedAt != refresh.UpdatedAt
                || await SessionWrittenSinceAsync(current, now - IdleLimit, ct))
            {
                return false;
            }

            await using (var scope = await actorFactory.CreateAsync(refresh.CreatedByUserId, ct))
            {
                await RefreshCleanup.ReleaseAsync(refresh, scope.Actors, files, logger);
            }

            await SaveAsync(
                refresh,
                r =>
                {
                    r.Status = WallRefreshStatus.Discarded;
                    r.Error = "Discarded after a day without activity.";
                    r.CompletedAt = DateTimeOffset.UtcNow;
                },
                ct);
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Whether the run's update session was written at or after <paramref name="since"/> (the full review is in use).</summary>
    private async Task<bool> SessionWrittenSinceAsync(WallRefresh refresh, DateTimeOffset since, CancellationToken ct)
    {
        if (refresh.UpdateSessionId is not { } sessionId)
        {
            return false;
        }

        await using var db = dbContextFactory.CreateDbContext();
        var written = await db.WallUpdateSessions.AsNoTracking()
            .Where(s => s.Id == sessionId)
            .Select(s => s.UpdatedAt)
            .ToListAsync(ct);
        return written.Any(at => at >= since);
    }
}
