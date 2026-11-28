// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.DeviceLinks;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests.DeviceLinks;

/// <summary>The one-time code: stored hashed, 60 seconds, single use, bound to the challenge, the device and the user.</summary>
public class DeviceLinkServiceTests
{
    private const string Verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
    private const string Device = "Test iPhone";

    private static readonly DeviceLinkRequest Request = new("state-0123456789abcdef", DeviceLinkPkce.Challenge(Verifier), Device);

    [Fact]
    public async Task Exchange_WithTheVerifier_MintsAWritableDeviceKeyNamedAfterTheDevice()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();
        var service = Service(h, new ManualTime());

        var code = await service.CreateCodeAsync(h.Owner.Id, Request);
        var result = await service.ExchangeAsync(code, Verifier, Device);

        Assert.NotNull(result);
        Assert.StartsWith("bwk_", result!.Token);
        Assert.Equal("iPhone: Test iPhone", result.Name);
        await using var db = h.CreateContext();
        Assert.Equal(DeviceLinkPkce.Hash(code), (await db.DeviceLinkCodes.SingleAsync()).CodeHash);
        Assert.DoesNotContain(await db.DeviceLinkCodes.Select(c => c.CodeHash).ToListAsync(), c => c == code);
        var key = await db.ApiKeys.IgnoreQueryFilters().SingleAsync(k => k.Id == result.KeyId);
        Assert.Equal(h.Owner.Id, key.UserId);
        Assert.Equal(ApiKeyScope.User, key.Scope);
        Assert.Null(key.WallId);
        Assert.True(key.AllowWrite);
        Assert.True(key.IsDevice);
        Assert.Null(key.ExpiresAt);
    }

    [Fact]
    public async Task Code_WorksOnlyOnce()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();
        var service = Service(h, new ManualTime());
        var code = await service.CreateCodeAsync(h.Owner.Id, Request);

        Assert.NotNull(await service.ExchangeAsync(code, Verifier, Device));
        Assert.Null(await service.ExchangeAsync(code, Verifier, Device));
    }

    [Fact]
    public async Task Code_ExpiresAfter60Seconds()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();
        var clock = new ManualTime();
        var service = Service(h, clock);
        var code = await service.CreateCodeAsync(h.Owner.Id, Request);

        clock.Advance(TimeSpan.FromSeconds(61));

        Assert.Null(await service.ExchangeAsync(code, Verifier, Device));
    }

    [Fact]
    public async Task Code_StillWorksJustBeforeItExpires()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();
        var clock = new ManualTime();
        var service = Service(h, clock);
        var code = await service.CreateCodeAsync(h.Owner.Id, Request);

        clock.Advance(TimeSpan.FromSeconds(59));

        Assert.NotNull(await service.ExchangeAsync(code, Verifier, Device));
    }

    [Fact]
    public async Task WrongVerifier_IsRefused_AndBurnsTheCode()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();
        var service = Service(h, new ManualTime());
        var code = await service.CreateCodeAsync(h.Owner.Id, Request);

        Assert.Null(await service.ExchangeAsync(code, Verifier[..^1] + "X", Device));
        Assert.Null(await service.ExchangeAsync(code, Verifier, Device));
        await using var db = h.CreateContext();
        Assert.Empty(await db.ApiKeys.IgnoreQueryFilters().Where(k => k.IsDevice).ToListAsync());
    }

    [Fact]
    public async Task OtherDevice_IsRefused()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();
        var service = Service(h, new ManualTime());
        var code = await service.CreateCodeAsync(h.Owner.Id, Request);

        Assert.Null(await service.ExchangeAsync(code, Verifier, "Another iPhone"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-code")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public async Task UnknownOrMalformedCode_IsRefused(string? code)
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();

        Assert.Null(await Service(h, new ManualTime()).ExchangeAsync(code, Verifier, Device));
    }

    [Fact]
    public async Task DeletedUser_GetsNoKey()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();
        var service = Service(h, new ManualTime());
        var code = await service.CreateCodeAsync(h.Owner.Id, Request);
        await using (var db = h.CreateContext())
        {
            await db.Users.Where(u => u.Id == h.Owner.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.DeletedAt, DateTimeOffset.UtcNow));
        }

        Assert.Null(await service.ExchangeAsync(code, Verifier, Device));
    }

    [Fact]
    public async Task SessionSignedInWithAnApiKey_CannotMakeACode()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();
        var keySession = Substitute.For<IApiKeySessionContext>();
        keySession.IsApiKeySession.Returns(true);
        var service = new DeviceLinkService(
            h.DbContextFactory, h.ApiKeyService, NullLogger<DeviceLinkService>.Instance, keySession, new ManualTime());

        await Assert.ThrowsAsync<ApiKeySessionRestrictedException>(() => service.CreateCodeAsync(h.Owner.Id, Request));
    }

    [Fact]
    public async Task OldRows_AreClearedWhenTheNextCodeIsMade()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();
        var clock = new ManualTime();
        var service = Service(h, clock);
        await service.CreateCodeAsync(h.Owner.Id, Request);

        clock.Advance(TimeSpan.FromDays(2));
        await service.CreateCodeAsync(h.Owner.Id, Request);

        await using var db = h.CreateContext();
        Assert.Equal(1, await db.DeviceLinkCodes.CountAsync());
    }

    private static DeviceLinkService Service(WallTestHarness h, ManualTime clock) =>
        new(h.DbContextFactory, h.ApiKeyService, NullLogger<DeviceLinkService>.Instance, null, clock);
}
