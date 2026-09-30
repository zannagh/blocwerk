// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Capture;

/// <summary>
/// The single consumer of <see cref="WallTextureRerenderQueue"/>: renders wall textures again one capture at a time
/// (<see cref="WallCaptureProcessor.RerenderTexturesAsync"/>), beside the capture worker, which may be busy with an
/// hour-long photo-real training. On start it re-enqueues every re-render a previous process left marked.
/// </summary>
public sealed class WallTextureRerenderWorker(
    WallTextureRerenderQueue queue, WallCaptureProcessor processor, RootDbContextFactory dbContextFactory, ILogger<WallTextureRerenderWorker> logger)
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

    private async Task RecoverAsync(CancellationToken ct)
    {
        try
        {
            await using var db = dbContextFactory.CreateDbContext();
            var marked = await db.WallCaptures
                .Where(c => c.TexturesJobId != null && c.TexturesJobId.StartsWith(CaptureTextureOutcome.RerenderMark))
                .Select(c => c.Id)
                .ToListAsync(ct);
            foreach (var captureId in marked)
            {
                queue.Enqueue(captureId);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not recover texture re-renders on startup");
        }
    }
}
