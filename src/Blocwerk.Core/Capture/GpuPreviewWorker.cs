// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Runners;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Capture;

/// <summary>
/// The single consumer of <see cref="GpuPreviewQueue"/>: installs delivered previews one at a time
/// (<see cref="WallCaptureProcessor.InstallPreviewAsync"/>), beside the capture worker. On start it re-enqueues every
/// preview a previous process left pending.
/// </summary>
public sealed class GpuPreviewWorker(
    GpuPreviewQueue queue, WallCaptureProcessor processor, GpuJobQueue jobs, ILogger<GpuPreviewWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            Guid jobId;
            try
            {
                jobId = await queue.DequeueAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                await processor.InstallPreviewAsync(jobId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Installing the preview of GPU job {JobId} failed; continuing", jobId);
            }
        }
    }

    private async Task RecoverAsync(CancellationToken ct)
    {
        try
        {
            foreach (var jobId in await jobs.PendingPreviewsAsync(ct))
            {
                queue.Enqueue(jobId);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not recover pending photo-real previews on startup");
        }
    }
}
