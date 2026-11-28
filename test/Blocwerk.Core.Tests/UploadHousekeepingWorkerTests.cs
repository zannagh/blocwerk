// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Services;
using Blocwerk.Core.Services.DeviceWrites;
using Microsoft.Extensions.DependencyInjection;

namespace Blocwerk.Core.Tests;

/// <summary>The scheduled sweep: stale upload temp files go, a running upload's file stays.</summary>
public sealed class UploadHousekeepingWorkerTests
{
    [Fact]
    public async Task TheSweep_DeletesStaleTempFiles_OfBothStores_AndKeepsFreshOnes()
    {
        using var h = new WallTestHarness();
        var now = DateTimeOffset.UtcNow;
        var staleClip = Temp(h.BetaStorage.CreateTempPath(".mp4"), now - UploadHousekeepingWorker.TempMaxAge - TimeSpan.FromMinutes(1));
        var stalePhoto = Temp(h.WallImageStorage.CreateTempPath(".jpg"), now - TimeSpan.FromDays(2));
        var running = Temp(h.BetaStorage.CreateTempPath(".mp4"), now - TimeSpan.FromMinutes(10));
        await using var services = new ServiceCollection()
            .AddSingleton<IBetaVideoStorage>(h.BetaStorage)
            .AddSingleton<IWallImageStorage>(h.WallImageStorage)
            .AddSingleton(new DeviceWriteReceiptService(h.DbContextFactory))
            .BuildServiceProvider();

        var (files, receipts) = await UploadHousekeepingWorker.SweepOnceAsync(services, now, default);

        Assert.Equal((2, 0), (files, receipts));
        Assert.False(File.Exists(staleClip));
        Assert.False(File.Exists(stalePhoto));
        Assert.True(File.Exists(running));
    }

    private static string Temp(string path, DateTimeOffset written)
    {
        File.WriteAllBytes(path, [1, 2, 3]);
        File.SetLastWriteTimeUtc(path, written.UtcDateTime);
        return path;
    }
}
