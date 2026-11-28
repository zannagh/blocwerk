// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Blocwerk.Core.Services.DeviceWrites;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The replay keys of the companion app's creating writes: a new key runs once, its retry replays the stored row, a
/// running one is in flight, a failed one runs again, an abandoned one is taken over, and a key is per user.
/// </summary>
public sealed class DeviceWriteReceiptServiceTests
{
    [Fact]
    public async Task AKey_RunsOnce_ThenReplaysItsRow_AndIsPerUser()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var receipts = new DeviceWriteReceiptService(h.DbContextFactory);
        var key = Guid.NewGuid();
        var scope = Guid.NewGuid();
        var row = Guid.NewGuid();

        Assert.Equal(DeviceWriteClaimState.Fresh, (await receipts.ClaimAsync(h.Owner.Id, key, "gallery", scope, default)).State);
        Assert.Equal(DeviceWriteClaimState.InFlight, (await receipts.ClaimAsync(h.Owner.Id, key, "gallery", scope, default)).State);
        await receipts.CompleteAsync(h.Owner.Id, key, row, default);

        Assert.Equal(new DeviceWriteClaim(DeviceWriteClaimState.Replay, row), await receipts.ClaimAsync(h.Owner.Id, key, "gallery", scope, default));
        Assert.Equal(DeviceWriteClaimState.Mismatch, (await receipts.ClaimAsync(h.Owner.Id, key, "beta", scope, default)).State);
        Assert.Equal(DeviceWriteClaimState.Mismatch, (await receipts.ClaimAsync(h.Owner.Id, key, "gallery", Guid.NewGuid(), default)).State);

        var other = await h.AddMemberAsync("other@test", Enums.WallRole.Member);
        Assert.Equal(DeviceWriteClaimState.Fresh, (await receipts.ClaimAsync(other.Id, key, "gallery", scope, default)).State);
    }

    [Fact]
    public async Task AFailedWrite_ReleasesItsKey_SoTheRetryRunsAgain()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var receipts = new DeviceWriteReceiptService(h.DbContextFactory);
        var key = Guid.NewGuid();

        await receipts.ClaimAsync(h.Owner.Id, key, "beta", Guid.Empty, default);
        await receipts.ReleaseAsync(h.Owner.Id, key);

        Assert.Equal(DeviceWriteClaimState.Fresh, (await receipts.ClaimAsync(h.Owner.Id, key, "beta", Guid.Empty, default)).State);
    }

    [Fact]
    public async Task AnAbandonedKey_IsTakenOver_AfterItsTimeout()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var clock = new MutableTestClock(new DateTimeOffset(2026, 11, 28, 12, 0, 0, TimeSpan.Zero));
        var receipts = new DeviceWriteReceiptService(h.DbContextFactory, clock);
        var key = Guid.NewGuid();

        await receipts.ClaimAsync(h.Owner.Id, key, "boulder", Guid.Empty, default);
        clock.Advance(DeviceWriteReceiptService.AbandonedAfter - TimeSpan.FromSeconds(1));
        Assert.Equal(DeviceWriteClaimState.InFlight, (await receipts.ClaimAsync(h.Owner.Id, key, "boulder", Guid.Empty, default)).State);
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(DeviceWriteClaimState.Fresh, (await receipts.ClaimAsync(h.Owner.Id, key, "boulder", Guid.Empty, default)).State);
        Assert.Equal(DeviceWriteClaimState.InFlight, (await receipts.ClaimAsync(h.Owner.Id, key, "boulder", Guid.Empty, default)).State);
    }

    [Fact]
    public async Task NoKey_IsAlwaysFresh_AndLeavesNothing_AndTheEmptyKeyIsRefused()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var receipts = new DeviceWriteReceiptService(h.DbContextFactory);

        Assert.Equal(DeviceWriteClaimState.Fresh, (await receipts.ClaimAsync(h.Owner.Id, null, "gallery", Guid.Empty, default)).State);
        await receipts.CompleteAsync(h.Owner.Id, null, Guid.NewGuid(), default);
        await using (var db = h.CreateContext())
        {
            Assert.Empty(await db.Set<DeviceWriteReceipt>().ToListAsync());
        }

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => receipts.ClaimAsync(h.Owner.Id, Guid.Empty, "gallery", Guid.Empty, default));
    }

    [Fact]
    public async Task TheSweep_DropsReceiptsPastTheirRetention_Only()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var clock = new MutableTestClock(new DateTimeOffset(2026, 11, 28, 12, 0, 0, TimeSpan.Zero));
        var receipts = new DeviceWriteReceiptService(h.DbContextFactory, clock);
        var old = Guid.NewGuid();
        await receipts.ClaimAsync(h.Owner.Id, old, "gallery", Guid.Empty, default);
        clock.Advance(DeviceWriteReceiptService.Retention);
        var fresh = Guid.NewGuid();
        await receipts.ClaimAsync(h.Owner.Id, fresh, "gallery", Guid.Empty, default);
        clock.Advance(TimeSpan.FromMinutes(1));

        Assert.Equal(1, await receipts.SweepAsync(default));
        await using var db = h.CreateContext();
        Assert.Equal(fresh, Assert.Single(await db.Set<DeviceWriteReceipt>().ToListAsync()).ClientRequestId);
    }

    [Fact]
    public void AnUnfinishedReceipt_IsTakenOver_OnlyPastTheRequestDeadline()
    {
        Assert.True(DeviceWriteReceiptService.AbandonedAfter > DeviceWriteReceiptService.RequestTimeout);
    }
}
