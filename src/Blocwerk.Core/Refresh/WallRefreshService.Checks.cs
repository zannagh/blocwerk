// <copyright file="WallRefreshService.Checks.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Refresh;

/// <summary>
/// The confirm screen's cards: listing them, their pictures, and the user's answers. An answer is written to the update
/// session as the user (the decision the full review would record), then the summary is made again on the worker
/// (<see cref="WallRefresh.SummaryRequestedAt"/>) so Apply promotes what the screen shows.
/// </summary>
public sealed partial class WallRefreshService
{
    public async Task<IReadOnlyList<RefreshCheck>> GetChecksAsync(Guid refreshId)
    {
        var (db, refresh) = await OpenRefreshAsync(refreshId);
        await using (db)
        {
            if (refresh.Status != WallRefreshStatus.ReadyToApply || refresh.UpdateSessionId is not { } sessionId)
            {
                return [];
            }

            var cards = await WallUpdateSessionService.ReadExceptionsAsync(db, sessionId);
            var oldIds = cards.Select(c => c.OldHoldId).OfType<Guid>().ToList();
            var holds = await db.Holds.AsNoTracking().Where(h => oldIds.Contains(h.Id))
                .Select(h => new { h.Id, h.Name, h.Color }).ToDictionaryAsync(h => h.Id);
            var boulders = (await db.BoulderHolds.AsNoTracking()
                    .Where(bh => oldIds.Contains(bh.HoldId) && !bh.Boulder.IsHistoric)
                    .Select(bh => new { bh.HoldId, bh.BoulderId })
                    .ToListAsync())
                .GroupBy(bh => bh.HoldId)
                .ToDictionary(g => g.Key, g => g.Select(bh => bh.BoulderId).Distinct().Count());
            return cards.Select(c =>
            {
                var hold = c.OldHoldId is { } id ? holds.GetValueOrDefault(id) : null;
                return new RefreshCheck(
                    c.Id, c.Kind, c.Status, hold is not null, c.HasTexture,
                    c.OldHoldId is { } o ? boulders.GetValueOrDefault(o) : 0, hold?.Name ?? hold?.Color, c.Confidence);
            }).ToList();
        }
    }

    public async Task<byte[]?> GetCheckCropAsync(Guid refreshId, Guid checkId, CheckCropView view, CancellationToken ct)
    {
        var (db, refresh) = await OpenRefreshAsync(refreshId);
        await using (db)
        {
            return refresh.UpdateSessionId is { } sessionId
                ? await RefreshCheckCrops.GetAsync(db, files, sessionId, checkId, view, ct)
                : null;
        }
    }

    public async Task DecideCheckAsync(Guid refreshId, Guid checkId, UpdateExceptionAnswer answer)
    {
        var (db, refresh) = await OpenRefreshAsync(refreshId);
        await using (db)
        {
            EnsureAnswerable(refresh);
        }

        var user = await currentUserService.GetCurrentUserAsync();
        await using (var scope = await actorFactory.CreateAsync(user.Id, CancellationToken.None))
        {
            var open = await scope.Actors.Sessions.GetOpenSessionAsync(refresh.WallId);
            if (open is null || open.Id != refresh.UpdateSessionId)
            {
                throw new UserFacingException("This update was replaced or discarded in the full review.");
            }

            await scope.Actors.Sessions.DecideUpdateExceptionAsync(refresh.WallId, checkId, answer);
        }

        await RequestSummaryAsync(refreshId);
        queue.Enqueue(refreshId);
    }

    private static void EnsureAnswerable(WallRefresh refresh)
    {
        if (refresh.Status != WallRefreshStatus.ReadyToApply)
        {
            throw new UserFacingException("This update can no longer be changed here.");
        }

        if (WallRefreshProcessor.IsRechecking(refresh, DateTimeOffset.UtcNow))
        {
            throw new UserFacingException("The update is being checked against the new 3D model. Try again in a moment.");
        }
    }

    private async Task RequestSummaryAsync(Guid refreshId)
    {
        var (db, row) = await OpenRefreshAsync(refreshId);
        await using (db)
        {
            row.SummaryRequestedAt = DateTimeOffset.UtcNow;
            row.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }
    }
}
