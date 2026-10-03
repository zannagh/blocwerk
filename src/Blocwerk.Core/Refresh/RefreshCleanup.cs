// <copyright file="RefreshCleanup.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Capture;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Services;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Refresh;

/// <summary>
/// Releases what a run holds when it is discarded, fails or goes stale: its unstarted capture draft (photos), its
/// staged panel update (unless it is being applied) and its kept videos. Acts as the run's starter, who owns the
/// draft. Each part is best effort: a part that is already gone is not an error.
/// </summary>
internal static class RefreshCleanup
{
    public static async Task ReleaseAsync(
        WallRefresh refresh, WallRefreshActors actors, ICaptureFileStore files, ILogger logger, bool keepDraft = false)
    {
        if (!keepDraft && !refresh.CaptureStarted && refresh.CaptureId is { } captureId)
        {
            await QuietlyAsync(() => actors.Capture.DiscardDraftAsync(captureId), refresh, "draft", logger);
        }

        await ReleaseStagedAsync(refresh, refresh.UpdateSessionId, actors, logger);
        foreach (var video in RefreshTimeline.Videos(refresh))
        {
            files.Delete(video.StoredName);
        }
    }

    /// <summary>Discards the run's staged panel update, if it is still the wall's open one.</summary>
    public static async Task ReleaseStagedAsync(WallRefresh refresh, Guid? sessionId, WallRefreshActors actors, ILogger logger)
    {
        if (sessionId is { } id)
        {
            await QuietlyAsync(() => actors.BigUpdate.DiscardAsync(refresh.WallId, id), refresh, "staged update", logger);
        }
    }

    private static async Task QuietlyAsync(Func<Task> action, WallRefresh refresh, string what, ILogger logger)
    {
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is UserFacingException or InvalidOperationException or UnauthorizedAccessException)
        {
            // Already gone, replaced by another update, or left for the capture's draft sweep (a day).
            logger.LogInformation(ex, "Wall refresh {RefreshId}: its {What} was left as it is", refresh.Id, what);
        }
    }
}
