// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Enums;
using Blocwerk.Core.Services.DeviceWrites;
using Blocwerk.Web.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

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

    private static DeviceCreatedWall Created(IActionResult result) =>
        Assert.IsType<DeviceCreatedWall>(Assert.IsType<OkObjectResult>(result).Value);

    private static DeviceWallsController Controller(WallTestHarness h) =>
        new(h.DbContextFactory, h.CurrentUser, h.WallService, new DeviceWriteReceiptService(h.DbContextFactory))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = DeviceKeyTestPrincipals.Device() },
            },
        };
}
