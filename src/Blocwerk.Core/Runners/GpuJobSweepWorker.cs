// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Runners;

/// <summary>
/// Runs <see cref="GpuJobQueue.SweepAsync"/> every <see cref="GpuRunnerOptions.SweepInterval"/>; with
/// <see cref="GpuRunnerMode.Off"/> it first cancels every waiting or running job.
/// </summary>
public sealed class GpuJobSweepWorker(GpuJobQueue queue, ILogger<GpuJobSweepWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (queue.Options.Mode == GpuRunnerMode.Off)
        {
            try
            {
                // Nothing may train them any more, and their bundles are copies of capture photos.
                await queue.CancelAllActiveAsync("3D runners were turned off on this server", stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not cancel the waiting GPU jobs after runners were turned off");
            }
        }

        using var timer = new PeriodicTimer(queue.Options.SweepInterval);
        do
        {
            try
            {
                await queue.SweepAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "The GPU job sweep failed; retrying next round");
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
