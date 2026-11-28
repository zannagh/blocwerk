// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Blocwerk.Core.Services.DeviceWrites;
using Blocwerk.Web.Controllers;
using Blocwerk.Web.Endpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// <c>POST /api/v1/walls</c>: the web's "+ New Wall" through <c>CreateWallAsync</c>, once per required
/// <c>clientRequestId</c>; the caller owns the new wall and is its admin.
/// </summary>
public sealed class DeviceWallsControllerTests
{
    [Fact]
    public async Task ACreate_MakesOneWall_OwnedByTheCaller_EvenWhenRetried()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var key = Guid.NewGuid();
        var body = new DeviceCreateWallRequest { ClientRequestId = key, Name = " Attic ", Description = "Board", Angle = 40 };

        var first = Created(await Controller(h).Create(body, default));
        var retry = Created(await Controller(h).Create(body, default));

        Assert.Equal(first, retry);
        Assert.Equal(("Attic", "Board", 40), (first.Name, first.Description, first.Angle));
        await using var db = h.CreateContext();
        var wall = await db.Walls.IgnoreQueryFilters().Include(w => w.Members).SingleAsync(w => w.Id == first.WallId);
        Assert.Equal(h.Owner.Id, wall.OwnerId);
        Assert.Equal(WallRole.Admin, Assert.Single(wall.Members).Role);
    }

    [Theory]
    [InlineData(null, "Attic", 10)]
    [InlineData("KEY", " ", 10)]
    [InlineData("KEY", "Attic", 91)]
    [InlineData("KEY", "Attic", -1)]
    [InlineData("00000000-0000-0000-0000-000000000000", "Attic", 10)]
    public async Task ABadBody_IsA400_AndMakesNoWall(string? key, string name, int angle)
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var body = new DeviceCreateWallRequest
        {
            ClientRequestId = key is null ? null : key == "KEY" ? Guid.NewGuid() : Guid.Parse(key),
            Name = name,
            Angle = angle,
        };

        var refused = Assert.IsType<ObjectResult>(await Controller(h).Create(body, default));
        Assert.Equal(400, refused.StatusCode);
        await using var db = h.CreateContext();
        Assert.Equal(1, await db.Walls.IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    public async Task ARequestCancelledAfterTheWallExists_KeepsItsReceipt_SoTheRetryMakesNoSecondWall()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        using var cancel = new CancellationTokenSource();
        var walls = Substitute.For<IWallService>();
        walls.CreateWallAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int>()).Returns(async call =>
        {
            var wall = await h.WallService.CreateWallAsync(call.ArgAt<string>(0), call.ArgAt<string?>(1), call.ArgAt<int>(2));
            await cancel.CancelAsync(); // the client gives up right after the wall exists
            return wall;
        });
        var body = new DeviceCreateWallRequest { ClientRequestId = Guid.NewGuid(), Name = "Attic" };

        var first = Created(await Controller(h, walls).Create(body, cancel.Token));
        var retry = Created(await Controller(h).Create(body, default));

        Assert.Equal(first.WallId, retry.WallId);
        await using var db = h.CreateContext();
        Assert.Equal(2, await db.Walls.IgnoreQueryFilters().CountAsync(w => w.OwnerId == h.Owner.Id));
    }

    [Fact]
    public async Task TheOwnedWallCap_Is409_ForTheAppAndTheWeb()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0); // the owner owns one wall
        var capped = new WallService(
            h.DbContextFactory, h.CurrentUser, h.HoldDetection, h.ActivityLog, NullLogger<WallService>.Instance,
            creationOptions: new WallCreationOptions { MaxOwnedPerUser = 1 });

        var refused = Assert.IsType<ObjectResult>(await Controller(h, capped).Create(new DeviceCreateWallRequest { ClientRequestId = Guid.NewGuid(), Name = "Two" }, default));
        Assert.Equal(409, refused.StatusCode);
        Assert.Equal(WallService.WallLimitMessage, Assert.IsType<ApiErrorResponse>(refused.Value).Message);
        var web = await Assert.ThrowsAsync<UserFacingException>(() => capped.CreateWallAsync("Two", null));
        Assert.Equal(WallService.WallLimitMessage, web.Message);
    }

    [Fact]
    public async Task TheKeysWallBucket_Is429_ButAReplayNeverSpendsIt()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        using var limiter = new DeviceKeyWallCreateRateLimiter(burst: 1, refill: TimeSpan.FromHours(1));
        var key = DeviceKeyTestPrincipals.Device();
        var body = new DeviceCreateWallRequest { ClientRequestId = Guid.NewGuid(), Name = "One" };

        Created(await Controller(h, limiter: limiter, key: key).Create(body, default));
        Created(await Controller(h, limiter: limiter, key: key).Create(body, default)); // the replay
        var refused = Assert.IsType<ObjectResult>(await Controller(h, limiter: limiter, key: key)
            .Create(new DeviceCreateWallRequest { ClientRequestId = Guid.NewGuid(), Name = "Two" }, default));
        Assert.Equal(429, refused.StatusCode);
    }

    [Fact]
    public async Task AReplay_OfAWallTheCallerNoLongerBelongsTo_Is404()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var body = new DeviceCreateWallRequest { ClientRequestId = Guid.NewGuid(), Name = "Handed over" };
        var wallId = Created(await Controller(h).Create(body, default)).WallId;
        var other = await h.AddMemberAsync("other@test", WallRole.Admin);
        await using (var db = h.CreateContext())
        {
            await db.Walls.IgnoreQueryFilters().Where(w => w.Id == wallId).ExecuteUpdateAsync(u => u.SetProperty(w => w.OwnerId, other.Id));
            await db.WallMembers.Where(m => m.WallId == wallId && m.UserId == h.Owner.Id).ExecuteDeleteAsync();
        }

        Assert.IsType<NotFoundResult>(await Controller(h).Create(body, default));
    }

    private static DeviceCreatedWall Created(IActionResult result) =>
        Assert.IsType<DeviceCreatedWall>(Assert.IsType<OkObjectResult>(result).Value);

    private static DeviceWallsController Controller(
        WallTestHarness h,
        IWallService? walls = null,
        DeviceKeyWallCreateRateLimiter? limiter = null,
        System.Security.Claims.ClaimsPrincipal? key = null) =>
        new(h.DbContextFactory, h.CurrentUser, walls ?? h.WallService, new DeviceWriteReceiptService(h.DbContextFactory), limiter ?? new DeviceKeyWallCreateRateLimiter())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = key ?? DeviceKeyTestPrincipals.Device() },
            },
        };
}
