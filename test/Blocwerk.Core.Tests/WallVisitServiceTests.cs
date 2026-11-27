// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Blocwerk.Core.Visits;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>Making a visitor link: who may, what is stored, and the hourly cap.</summary>
public class WallVisitServiceTests
{
    [Fact]
    public async Task Create_StoresOnlyTheHashBoundToTheWallAndItsCreatorFor24Hours()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();
        var member = await h.AddMemberAsync("member@test", WallRole.Member);
        h.ActingUser = member;
        var clock = new VisitTestClock(VisitFixture.Start);

        var created = await VisitFixture.Service(h, clock).CreateAsync(h.WallId);

        await using var db = h.CreateContext();
        var row = await db.WallVisitLinks.SingleAsync();
        Assert.Equal(VisitLinkTokens.Hash(created.Token), row.TokenHash);
        Assert.NotEqual(created.Token, row.TokenHash);
        Assert.Equal(h.WallId, row.WallId);
        Assert.Equal(member.Id, row.CreatedByUserId);
        Assert.Equal(VisitFixture.Start, row.CreatedAt);
        Assert.Equal(VisitFixture.Start.AddHours(24), row.ExpiresAt);
        Assert.Equal(row.ExpiresAt, created.ExpiresAt);
        Assert.Null(row.RedeemedAt);
        Assert.Null(row.RedemptionId);
    }

    [Theory]
    [InlineData(WallRole.Member)]
    [InlineData(WallRole.Moderator)]
    [InlineData(WallRole.Admin)]
    public async Task Create_IsOpenToEveryRoleOfTheWall(WallRole role)
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();
        h.ActingUser = await h.AddMemberAsync($"{role}@test", role);

        var created = await VisitFixture.Service(h, new VisitTestClock(VisitFixture.Start)).CreateAsync(h.WallId);

        Assert.True(VisitLinkTokens.IsWellFormed(created.Token));
    }

    [Fact]
    public async Task Create_IsOpenToTheOwnerAndToASiteAdmin()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();
        var clock = new VisitTestClock(VisitFixture.Start);
        await VisitFixture.Service(h, clock).CreateAsync(h.WallId);

        var siteAdmin = await AddOutsiderAsync(h, "admin@test", IdentityRole.Admin);
        h.ActingUser = siteAdmin;
        await VisitFixture.Service(h, clock).CreateAsync(h.WallId);

        await using var db = h.CreateContext();
        Assert.Equal(2, await db.WallVisitLinks.CountAsync());
    }

    [Fact]
    public async Task Create_RefusesASignedInNonMember()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();
        h.ActingUser = await AddOutsiderAsync(h, "stranger@test", IdentityRole.User);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => VisitFixture.Service(h, new VisitTestClock(VisitFixture.Start)).CreateAsync(h.WallId));
    }

    [Fact]
    public async Task Create_RefusesAnAnonymousCallerAndAVisitor()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();
        VisitFixture.GoAnonymous(h);
        var clock = new VisitTestClock(VisitFixture.Start);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => VisitFixture.Service(h, clock).CreateAsync(h.WallId));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => VisitFixture.Service(h, clock, VisitFixture.Visitor(h.WallId)).CreateAsync(h.WallId));
    }

    [Fact]
    public async Task Create_RefusesAKioskSessionEvenActingAsTheWallAdmin()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();
        var kiosk = Substitute.For<IKioskContext>();
        kiosk.IsKiosk.Returns(true);
        kiosk.KioskWallId.Returns(h.WallId);

        await Assert.ThrowsAsync<KioskRestrictedException>(
            () => VisitFixture.Service(h, new VisitTestClock(VisitFixture.Start), kiosk).CreateAsync(h.WallId));
    }

    [Fact]
    public async Task Create_CapsAMemberAtTwentyAnHour()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();
        var clock = new VisitTestClock(VisitFixture.Start);
        var service = VisitFixture.Service(h, clock);
        for (var i = 0; i < WallVisitService.HourlyLimit; i++)
        {
            await service.CreateAsync(h.WallId);
        }

        await Assert.ThrowsAsync<VisitLinkLimitException>(() => service.CreateAsync(h.WallId));

        clock.Advance(TimeSpan.FromMinutes(61));
        await service.CreateAsync(h.WallId);
    }

    [Fact]
    public async Task Create_CleansUpLinksAWeekPastTheirExpiry()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();
        var clock = new VisitTestClock(VisitFixture.Start);
        var service = VisitFixture.Service(h, clock);
        await service.CreateAsync(h.WallId);

        clock.Advance(WallVisitService.Lifetime + WallVisitService.Retention + TimeSpan.FromMinutes(1));
        var fresh = await service.CreateAsync(h.WallId);

        await using var db = h.CreateContext();
        var row = await db.WallVisitLinks.SingleAsync();
        Assert.Equal(VisitLinkTokens.Hash(fresh.Token), row.TokenHash);
    }

    private static async Task<User> AddOutsiderAsync(WallTestHarness h, string identifier, IdentityRole role)
    {
        await using var db = h.CreateContext();
        var user = new User { Identifier = identifier, DisplayName = identifier, Role = role };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }
}
