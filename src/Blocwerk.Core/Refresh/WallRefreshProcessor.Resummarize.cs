// <copyright file="WallRefreshProcessor.Resummarize.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Refresh;

/// <summary>
/// An answer on the confirm screen changed the decisions (<see cref="WallRefresh.SummaryRequestedAt"/>): the summary
/// is made again from the decisions exactly as Apply reads them, so its version is what Apply checks, and the user
/// applies what the screen shows. Nothing is applied here. A failure keeps the old summary; Apply then sees that it is
/// out of date and shows the new one first.
/// </summary>
public sealed partial class WallRefreshProcessor
{
    /// <summary>A request older than this is taken as lost (the job never ran): Apply stops waiting for it.</summary>
    public static readonly TimeSpan ResummarizeStale = TimeSpan.FromMinutes(10);

    /// <summary>Whether an answer's summary is waiting to be made (the worker makes it, however old the request).</summary>
    public static bool HasSummaryRequest(WallRefresh refresh) =>
        refresh.Status == WallRefreshStatus.ReadyToApply && refresh.SummaryRequestedAt is not null;

    /// <summary>Whether an answer's summary is still being made (Apply waits for it), unless the request went stale.</summary>
    public static bool IsResummarizing(WallRefresh refresh, DateTimeOffset now) =>
        HasSummaryRequest(refresh) && now - refresh.SummaryRequestedAt < ResummarizeStale;

    private async Task ResummarizeAsync(WallRefresh refresh, WallRefreshActors actors, CancellationToken ct)
    {
        var requested = refresh.SummaryRequestedAt;
        RefreshSummary? fresh = null;
        try
        {
            var open = await actors.Sessions.GetOpenSessionAsync(refresh.WallId);
            if (open is not null && open.Id == refresh.UpdateSessionId && RefreshTimeline.Summary(refresh) is { } summary)
            {
                var matched = await actors.BigUpdate.ResumeAsync(refresh.WallId);
                var promotable = await RefreshDecisions.LoadAsync(refresh.WallId, actors, matched, await StagedHoldsByPanelAsync(refresh.WallId, ct));
                fresh = await FreshSummaryAsync(refresh, summary, promotable, ct) with { AnsweredAt = open.UpdatedAt };
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Wall refresh {RefreshId}: the summary could not be made again after an answer; Apply will show it first", refresh.Id);
        }

        await SaveAsync(
            refresh,
            r =>
            {
                if (fresh is not null)
                {
                    r.SummaryJson = RefreshTimeline.Write(fresh);
                }

                RefreshTimeline.Set(r, RefreshTimeline.Review, RefreshStepState.Waiting, "Check the summary, then apply");
            },
            CancellationToken.None);
        await ClearRequestAsync(refresh, requested);
    }

    /// <summary>Clears the request this run served; a newer answer meanwhile keeps it (its own enqueue makes the summary again).</summary>
    private async Task ClearRequestAsync(WallRefresh refresh, DateTimeOffset? served)
    {
        // Compare-and-clear in one statement: an answer written between a read and a write must not be lost.
        await using var db = dbContextFactory.CreateDbContext();
        var cleared = await db.WallRefreshes
            .Where(r => r.Id == refresh.Id && r.SummaryRequestedAt == served)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.SummaryRequestedAt, (DateTimeOffset?)null));
        if (cleared > 0)
        {
            refresh.SummaryRequestedAt = null;
        }
    }
}
