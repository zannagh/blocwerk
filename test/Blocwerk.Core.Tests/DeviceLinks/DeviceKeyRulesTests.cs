// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.DeviceLinks;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests.DeviceLinks;

/// <summary>Device keys: one per device name, at most five live, and none that sat unused for 90 days.</summary>
public class DeviceKeyRulesTests
{
    [Fact]
    public async Task LinkingTheSameDeviceAgain_RevokesItsEarlierKey()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();

        var (first, firstToken) = await h.ApiKeyService.CreateDeviceKeyAsync(h.Owner.Id, "iPhone: A");
        var (other, _) = await h.ApiKeyService.CreateDeviceKeyAsync(h.Owner.Id, "iPhone: B");
        var (second, secondToken) = await h.ApiKeyService.CreateDeviceKeyAsync(h.Owner.Id, "iPhone: A");

        Assert.Null(await h.ApiKeyService.FindActiveAsync(firstToken));
        Assert.NotNull(await h.ApiKeyService.FindActiveAsync(secondToken));
        Assert.Null((await ReadAsync(h, other.Id)).RevokedAt);
        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public async Task ASixthDevice_RevokesTheOldestDeviceKey_AndLeavesPlainKeysAlone()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();
        var (plain, _) = await h.ApiKeyService.CreateUserKeyAsync(h.Owner.Id, h.Owner.Id, "Script", null, allowWrite: true);
        var ids = new List<Guid>();
        for (var i = 0; i < DeviceLinkPolicy.MaxLiveDeviceKeys + 1; i++)
        {
            ids.Add((await h.ApiKeyService.CreateDeviceKeyAsync(h.Owner.Id, $"iPhone: {i}")).Key.Id);
            await Task.Delay(5);
        }

        await using var db = h.CreateContext();
        var live = await db.ApiKeys.IgnoreQueryFilters()
            .Where(k => k.UserId == h.Owner.Id && k.IsDevice && k.RevokedAt == null)
            .Select(k => k.Id)
            .ToListAsync();
        Assert.Equal(DeviceLinkPolicy.MaxLiveDeviceKeys, live.Count);
        Assert.DoesNotContain(ids[0], live);
        Assert.Null((await ReadAsync(h, plain.Id)).RevokedAt);
    }

    [Theory]
    [InlineData(true, 89, true)]
    [InlineData(true, 91, false)]
    [InlineData(false, 400, true)]
    public async Task ADeviceKey_StopsWorkingAfter90DaysUnused(bool isDevice, int daysIdle, bool works)
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();
        var (key, token) = isDevice
            ? await h.ApiKeyService.CreateDeviceKeyAsync(h.Owner.Id, "iPhone: Idle")
            : await h.ApiKeyService.CreateUserKeyAsync(h.Owner.Id, h.Owner.Id, "Script", null);
        var then = DateTimeOffset.UtcNow.AddDays(-daysIdle);
        await using (var db = h.CreateContext())
        {
            await db.ApiKeys.IgnoreQueryFilters().Where(k => k.Id == key.Id).ExecuteUpdateAsync(s => s
                .SetProperty(k => k.CreatedAt, then.AddDays(-1))
                .SetProperty(k => k.LastUsedAt, then));
        }

        Assert.Equal(works, await h.ApiKeyService.FindActiveAsync(token) is not null);
    }

    [Fact]
    public void IdleExpiry_FallsBackToCreationWhenNeverUsed()
    {
        var now = DateTimeOffset.UtcNow;
        var key = new ApiKey
        {
            Name = "iPhone: X",
            Scope = ApiKeyScope.User,
            KeyHash = "h",
            Prefix = "bwk_",
            IsDevice = true,
            CreatedAt = now.AddDays(-91),
        };

        Assert.True(DeviceLinkPolicy.IsIdleExpired(key, now));
    }

    private static async Task<ApiKey> ReadAsync(WallTestHarness h, Guid id)
    {
        await using var db = h.CreateContext();
        return await db.ApiKeys.IgnoreQueryFilters().SingleAsync(k => k.Id == id);
    }
}
