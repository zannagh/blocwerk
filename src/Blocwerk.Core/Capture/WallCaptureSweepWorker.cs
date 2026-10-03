using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Capture;

/// <summary>Runs <see cref="WallCaptureSweeper"/> shortly after start and then every <see cref="WallCapturePipelineOptions.SweepInterval"/>.</summary>
public sealed class WallCaptureSweepWorker(
    WallCaptureSweeper sweeper,
    WallCapturePipelineOptions options,
    ILogger<WallCaptureSweepWorker> logger) : BackgroundService
{
    private static readonly TimeSpan StartDelay = TimeSpan.FromMinutes(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartDelay, stoppingToken);
            using var timer = new PeriodicTimer(options.SweepInterval);
            do
            {
                try
                {
                    await sweeper.SweepAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Capture retention sweep failed; retrying at the next interval");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown.
        }
    }
}
