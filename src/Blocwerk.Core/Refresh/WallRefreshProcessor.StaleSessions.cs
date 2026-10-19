// <copyright file="WallRefreshProcessor.StaleSessions.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Refresh;

/// <summary>
/// Step-by-step updates ("Update step by step") left untouched for a week are discarded: their staged photos and
/// decisions are thrown away, as the wizard's own Discard does, and nothing live changes. An open update blocks every
/// "Update panels + 3D" run on its wall, so a forgotten one must not do that for ever. A session that belongs to a run
/// still in progress is the run's own sweep's business (<see cref="DiscardStaleAsync"/>) and is left alone here.
/// </summary>
public sealed partial class WallRefreshProcessor
{
    /// <summary>How long a step-by-step update may sit untouched before it is discarded.</summary>
    public static readonly TimeSpan SessionIdleLimit = TimeSpan.FromDays(7);

    private static readonly WallRefreshStatus[] ActiveStatuses =
    [
        WallRefreshStatus.Uploading, WallRefreshStatus.Sorting, WallRefreshStatus.ReadyToStart,
        WallRefreshStatus.Running, WallRefreshStatus.ReadyToApply, WallRefreshStatus.Applying,
    ];

    /// <summary>Discards the idle step-by-step updates; returns how many were discarded.</summary>
    public async Task<int> DiscardIdleSessionsAsync(DateTimeOffset now, CancellationToken ct)
    {
        List<WallUpdateSession> idle;
        await using (var db = dbContextFactory.CreateDbContext())
        {
            var ofRuns = db.WallRefreshes
                .Where(r => ActiveStatuses.Contains(r.Status) && r.UpdateSessionId != null)
                .Select(r => r.UpdateSessionId!.Value);

            // Few rows; filtered in memory because SQLite cannot compare a DateTimeOffset.
            idle = (await db.WallUpdateSessions.AsNoTracking()
                    .Where(s => s.Status == WallUpdateSessionStatus.Open && !ofRuns.Contains(s.Id))
                    .ToListAsync(ct))
                .Where(s => s.UpdatedAt < now - SessionIdleLimit)
                .ToList();
        }

        var discarded = 0;
        foreach (var session in idle)
        {
            discarded += await DiscardIdleSessionAsync(session, ct) ? 1 : 0;
        }

        return discarded;
    }

    private async Task<bool> DiscardIdleSessionAsync(WallUpdateSession session, CancellationToken ct)
    {
        using (await wallLocks.AcquireAsync(session.WallId, ct))
        {
            // Somebody may have come back to it, or a run may have picked it up, in the meantime.
            await using (var db = dbContextFactory.CreateDbContext())
            {
                var current = await db.WallUpdateSessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == session.Id, ct);
                var claimed = await db.WallRefreshes.AnyAsync(r => r.UpdateSessionId == session.Id && ActiveStatuses.Contains(r.Status), ct);
                if (current is null || current.Status != WallUpdateSessionStatus.Open || current.UpdatedAt != session.UpdatedAt || claimed)
                {
                    return false;
                }
            }

            if ((session.CreatedByUserId ?? session.LastActiveByUserId) is not { } actor)
            {
                return false;
            }

            try
            {
                await using var scope = await actorFactory.CreateAsync(actor, ct);
                await scope.Actors.BigUpdate.DiscardAsync(session.WallId, session.Id);
            }
            catch (Exception ex) when (ex is UserFacingException or InvalidOperationException or UnauthorizedAccessException)
            {
                // Replaced meanwhile, or its starter may no longer discard it: left for a person.
                logger.LogInformation(ex, "Idle wall update session {SessionId} on wall {WallId} was left as it is", session.Id, session.WallId);
                return false;
            }

            logger.LogInformation(
                "Discarded wall update session {SessionId} on wall {WallId}: untouched since {UpdatedAt}", session.Id, session.WallId, session.UpdatedAt);
            return true;
        }
    }
}
