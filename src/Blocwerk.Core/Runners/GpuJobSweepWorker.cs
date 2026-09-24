// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Runners;

/// <summary>Runs <see cref="GpuJobQueue.SweepAsync"/> every <see cref="GpuRunnerOptions.SweepInterval"/>.</summary>
public sealed class GpuJobSweepWorker(GpuJobQueue queue, ILogger<GpuJobSweepWorker> logger) : BackgroundService
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
