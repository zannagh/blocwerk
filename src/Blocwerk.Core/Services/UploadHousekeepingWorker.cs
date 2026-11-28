// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Services.DeviceWrites;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

/// <summary>
/// Every <see cref="Interval"/>: deletes the beta-video and gallery temp files older than <see cref="TempMaxAge"/> (an
/// upload that died before it could clean up after itself) and the companion app's write receipts past their retention
/// (<see cref="DeviceWriteReceiptService.SweepAsync"/>).
/// </summary>
public sealed class UploadHousekeepingWorker(IServiceScopeFactory scopes, ILogger<UploadHousekeepingWorker> logger) : BackgroundService
{
    /// <summary>How often the sweep runs.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    /// <summary>
    /// A temp file this old belongs to no running upload: a device upload has a 30-minute deadline, and a browser clip
    /// of the largest allowed size is far below this at any usable connection speed.
    /// </summary>
    public static readonly TimeSpan TempMaxAge = TimeSpan.FromHours(12);

    private static readonly TimeSpan StartDelay = TimeSpan.FromMinutes(3);

    /// <summary>One sweep; returns the temp files and receipts it deleted.</summary>
    public static async Task<(int TempFiles, int Receipts)> SweepOnceAsync(IServiceProvider services, DateTimeOffset now, CancellationToken ct)
    {
        var olderThan = now - TempMaxAge;
        var tempFiles = services.GetRequiredService<IBetaVideoStorage>().SweepTemp(olderThan)
            + services.GetRequiredService<IWallImageStorage>().SweepTemp(olderThan);
        var receipts = await services.GetRequiredService<DeviceWriteReceiptService>().SweepAsync(ct);
        return (tempFiles, receipts);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartDelay, stoppingToken);
            using var timer = new PeriodicTimer(Interval);
            do
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var (files, receipts) = await SweepOnceAsync(scope.ServiceProvider, DateTimeOffset.UtcNow, stoppingToken);
                    if (files + receipts > 0)
                    {
                        logger.LogInformation("Upload housekeeping removed {Files} temp files and {Receipts} write receipts", files, receipts);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Upload housekeeping failed; retrying at the next interval");
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
