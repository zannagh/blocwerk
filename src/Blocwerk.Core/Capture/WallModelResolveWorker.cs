// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Capture.FollowUp;
using Blocwerk.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Capture;

/// <summary>
/// The single consumer of <see cref="WallModelResolveQueue"/>: solves finished captures' 3D models again one at a time
/// (<see cref="WallCaptureProcessor.ResolveModelAsync"/>), beside the capture worker. On start it re-enqueues every
/// re-solve a previous process left marked, and every adopted one whose follow-ups it left unfinished.
/// </summary>
public sealed class WallModelResolveWorker(
    WallModelResolveQueue queue, WallCaptureProcessor processor, RootDbContextFactory dbContextFactory, ILogger<WallModelResolveWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            Guid captureId;
            try
            {
                captureId = await queue.DequeueAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                await processor.ResolveModelAsync(captureId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Solving the 3D model of capture {CaptureId} again failed; continuing", captureId);
            }
        }
    }

    private async Task RecoverAsync(CancellationToken ct)
    {
        try
        {
            await using var db = dbContextFactory.CreateDbContext();
            var marked = await db.WallCaptures
                .Where(c => (c.SolveJobId != null && c.SolveJobId.StartsWith(CaptureResolveMark.Mark))
                    || (c.FollowUpJson != null && c.FollowUpJson.Contains(CaptureFollowUpRecord.RederiveMarker)))
                .Select(c => c.Id)
                .ToListAsync(ct);
            foreach (var captureId in marked)
            {
                queue.Enqueue(captureId);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not recover model re-solves on startup");
        }
    }
}
