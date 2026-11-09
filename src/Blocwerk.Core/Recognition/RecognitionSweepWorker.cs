// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Recognition;

/// <summary>Runs <see cref="RecognitionQueue.SweepAsync"/> every <see cref="RecognitionQueueOptions.SweepInterval"/>.</summary>
public sealed class RecognitionSweepWorker(RecognitionQueue queue, ILogger<RecognitionSweepWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(queue.Options.SweepInterval);
        do
        {
            try
            {
                await queue.SweepAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "The recognition job sweep failed; retrying next round");
            }
        }
        while (await WaitAsync(timer, stoppingToken));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
