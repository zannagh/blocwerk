// <copyright file="WallRefreshProcessor.Stale.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Refresh;

/// <summary>
/// Runs left waiting for the user (uploading, sort screen, confirm screen) for a day are discarded: their draft
/// photos, staged panel update and videos are released, exactly as "Discard this update" does. Nothing live changes.
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

        foreach (var refresh in stale)
        {
            await DiscardIdleAsync(refresh, ct);
        }

        return stale.Count;
    }

    private async Task DiscardIdleAsync(WallRefresh refresh, CancellationToken ct)
    {
        var gate = wallLocks.GetOrAdd(refresh.WallId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            // The user may have come back in the meantime.
            var current = await LoadAsync(refresh.Id, ct);
            if (current is null || current.Status != refresh.Status || current.UpdatedAt != refresh.UpdatedAt)
            {
                return;
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
        }
        finally
        {
            gate.Release();
        }
    }
}
