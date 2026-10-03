// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Capture;

/// <summary>
/// The single consumer of <see cref="WallTextureRerenderQueue"/>: renders wall textures again one capture at a time
/// (<see cref="WallCaptureProcessor.RerenderTexturesAsync"/>), beside the capture worker, which may be busy with an
/// hour-long photo-real training. On start, and whenever it was idle for a while (<see cref="CaptureRedoRescan"/>), it
/// re-enqueues every re-render left marked that no run is working on.
/// </summary>
public sealed class WallTextureRerenderWorker(
    WallTextureRerenderQueue queue,
    WallCaptureProcessor processor,
    RootDbContextFactory dbContextFactory,
    ILogger<WallTextureRerenderWorker> logger,
    WallCapturePipelineOptions? options = null)
    : BackgroundService
{
    private readonly TimeSpan idle = (options ?? new WallCapturePipelineOptions()).RedoRescanInterval;

    /// <summary>Queues every marked re-render no run is working on. Public for tests.</summary>
    /// <param name="ct">Cancellation.</param>
    /// <returns>How many were queued.</returns>
    public async Task<int> RequeueStuckAsync(CancellationToken ct)
    {
        var stuck = await CaptureRedoRescan.StuckAsync(dbContextFactory, CaptureRedoKind.Rerender, processor, ct);
        stuck.ForEach(queue.Enqueue);
        return stuck.Count;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await TryRequeueAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            Guid captureId;
            try
            {
                if (await CaptureRedoRescan.NextAsync(queue.DequeueAsync, idle, stoppingToken) is not { } next)
                {
                    await TryRequeueAsync(stoppingToken);
                    continue;
                }

                captureId = next;
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                await processor.RerenderTexturesAsync(captureId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Rendering the textures of capture {CaptureId} again failed; continuing", captureId);
            }
        }
    }

    private async Task TryRequeueAsync(CancellationToken ct)
    {
        try
        {
            await RequeueStuckAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not look for texture re-renders left marked");
        }
    }
}
