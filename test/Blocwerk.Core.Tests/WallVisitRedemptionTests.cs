// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Visits;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>Using a visitor link: once, by one browser, until it expires, and for its own wall only.</summary>
public class WallVisitRedemptionTests
{
    [Fact]
    public async Task FirstUse_MarksTheLinkUsedAndGrantsTheWallUntilExpiry()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();
        var clock = new VisitTestClock(VisitFixture.Start);
        var service = VisitFixture.Service(h, clock);
        var created = await service.CreateAsync(h.WallId);
        Assert.Equal(VisitLinkStatus.Open, (await service.LookUpAsync(created.Token)).Status);

        clock.Advance(TimeSpan.FromHours(3));
        var grant = await service.RedeemAsync(created.Token);

        Assert.NotNull(grant);
        Assert.Equal(h.WallId, grant!.WallId);
        Assert.Equal(created.ExpiresAt, grant.ExpiresAt);
        await using var db = h.CreateContext();
        var row = await db.WallVisitLinks.SingleAsync();
        Assert.Equal(grant.RedemptionId, row.RedemptionId);
        Assert.Equal(grant.LinkId, row.Id);
        Assert.Equal(clock.Now, row.RedeemedAt);
    }

    [Fact]
    public async Task SameBrowserAgain_GoesBackToTheWall_AnyOtherBrowserIsToldItWasUsed()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();
        var service = VisitFixture.Service(h, new VisitTestClock(VisitFixture.Start));
        var created = await service.CreateAsync(h.WallId);
        var grant = await service.RedeemAsync(created.Token);

        var link = await service.LookUpAsync(created.Token);
        var stranger = grant! with { RedemptionId = Guid.NewGuid() };

        Assert.Equal(VisitLinkStatus.Redeemed, link.Status);
        Assert.Equal(VisitLandingState.GoToWall, VisitLanding.Decide(link, false, false, grant));
        Assert.Equal(VisitLandingState.Used, VisitLanding.Decide(link, false, false, null));
        Assert.Equal(VisitLandingState.Used, VisitLanding.Decide(link, false, false, stranger));
        Assert.Null(await service.RedeemAsync(created.Token));
    }

    [Fact]
    public async Task AfterExpiry_TheLinkIsExpired_UsedOrNot()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();
        var clock = new VisitTestClock(VisitFixture.Start);
        var service = VisitFixture.Service(h, clock);
        var unused = await service.CreateAsync(h.WallId);
        var used = await service.CreateAsync(h.WallId);
        var grant = await service.RedeemAsync(used.Token);

        clock.Advance(WallVisitService.Lifetime);

        Assert.Null(await service.RedeemAsync(unused.Token));
        var unusedLink = await service.LookUpAsync(unused.Token);
        var usedLink = await service.LookUpAsync(used.Token);
        Assert.Equal(VisitLinkStatus.Expired, unusedLink.Status);
        Assert.Equal(VisitLinkStatus.Expired, usedLink.Status);

        // Expired wins over used, even for the very browser that used it.
        Assert.Equal(VisitLandingState.Expired, VisitLanding.Decide(usedLink, false, false, grant));
        Assert.Equal(VisitLandingState.Expired, VisitLanding.Decide(unusedLink, false, false, null));
        Assert.False(grant!.IsActiveAt(clock.Now));
    }

    [Fact]
    public async Task ConcurrentFirstUses_ExactlyOneWins()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();
        var service = VisitFixture.Service(h, new VisitTestClock(VisitFixture.Start));
        var created = await service.CreateAsync(h.WallId);

        var attempts = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => service.RedeemAsync(created.Token))));

        var winner = Assert.Single(attempts, a => a is not null);
        await using var db = h.CreateContext();
        Assert.Equal(winner!.RedemptionId, (await db.WallVisitLinks.SingleAsync()).RedemptionId);
    }

    [Fact]
    public async Task UnknownOrMalformedToken_IsInvalidAndUsesNothing()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();
        var service = VisitFixture.Service(h, new VisitTestClock(VisitFixture.Start));

        Assert.Equal(VisitLandingState.Invalid, VisitLanding.Decide(await service.LookUpAsync(VisitLinkTokens.Create()), false, false, null));
        Assert.Equal(VisitLinkStatus.NotFound, (await service.LookUpAsync("../../etc")).Status);
        Assert.Null(await service.RedeemAsync(VisitLinkTokens.Create()));
    }

    [Fact]
    public async Task SignedIn_AMemberGoesToTheWallAndAnyoneElseIsToldItIsForGuests_TheLinkStaysUnused()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();
        var service = VisitFixture.Service(h, new VisitTestClock(VisitFixture.Start));
        var created = await service.CreateAsync(h.WallId);
        var link = await service.LookUpAsync(created.Token);

        Assert.True(await service.CallerIsMemberAsync(h.WallId));
        Assert.Equal(VisitLandingState.GoToWall, VisitLanding.Decide(link, true, true, null));
        Assert.Equal(VisitLandingState.SignedIn, VisitLanding.Decide(link, true, false, null));
        Assert.Equal(VisitLinkStatus.Open, (await service.LookUpAsync(created.Token)).Status);
    }

    [Fact]
    public async Task WrongWall_TheGrantOpensOnlyTheWallOfItsLink()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();
        var otherWall = await VisitFixture.SeedOtherWallAsync(h);
        var service = VisitFixture.Service(h, new VisitTestClock(VisitFixture.Start));
        var created = await service.CreateAsync(h.WallId);
        var grant = await service.RedeemAsync(created.Token);
        var visitor = VisitFixture.Visitor(grant!.WallId, grant.ExpiresAt);

        Assert.True(KioskViewing.AllowsAnonymousViewOf(visitor, h.WallId));
        Assert.False(KioskViewing.AllowsAnonymousViewOf(visitor, otherWall));

        // A cookie from one link never counts as the redemption of another link, even on the same wall.
        var second = await service.LookUpAsync((await service.CreateAsync(h.WallId)).Token);
        Assert.False(second.IsRedeemedBy(grant));
    }
}
