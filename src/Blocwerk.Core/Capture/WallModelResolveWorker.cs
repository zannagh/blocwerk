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
/// re-solve a previous process left marked, and every adopted one whose follow-ups it left unfinished (at most
/// <see cref="CaptureFollowUpChain.MaxRecoveries"/> starts in a row). When idle it re-enqueues marks no run clears
/// (<see cref="CaptureRedoRescan"/>).
/// </summary>
public sealed class WallModelResolveWorker(
    WallModelResolveQueue queue,
    WallCaptureProcessor processor,
    RootDbContextFactory dbContextFactory,
    ILogger<WallModelResolveWorker> logger,
    CaptureFollowUpChain? chain = null,
    WallCapturePipelineOptions? options = null)
    : BackgroundService
{
    private readonly TimeSpan idle = (options ?? new WallCapturePipelineOptions()).RedoRescanInterval;

    /// <summary>Queues every marked re-solve no run is working on. Public for tests.</summary>
    /// <param name="ct">Cancellation.</param>
    /// <returns>How many were queued.</returns>
    public async Task<int> RequeueStuckAsync(CancellationToken ct)
    {
        var stuck = await CaptureRedoRescan.StuckAsync(
            dbContextFactory, q => q.Where(c => c.SolveJobId != null && c.SolveJobId.StartsWith(CaptureResolveMark.Mark)), processor, ct);
        stuck.ForEach(queue.Enqueue);
        return stuck.Count;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            Guid captureId;
            try
            {
                if (await CaptureRedoRescan.NextAsync(queue.DequeueAsync, idle, stoppingToken) is not { } next)
                {
                    await RequeueStuckAsync(stoppingToken);
                    continue;
                }

                captureId = next;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not look for re-solves left marked");
                continue;
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
            await RequeueStuckAsync(ct);
            await using var db = dbContextFactory.CreateDbContext();
            var rederive = await db.WallCaptures
                .Where(c => c.FollowUpJson != null && c.FollowUpJson.Contains(CaptureFollowUpRecord.RederiveMarker))
                .Select(c => c.Id)
                .ToListAsync(ct);
            foreach (var captureId in rederive)
            {
                if (chain is null || await chain.CountRecoveryAsync(captureId, CaptureFollowUpRecoveryKind.Rederive, ct))
                {
                    queue.Enqueue(captureId);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not recover model re-solves on startup");
        }
    }
}
