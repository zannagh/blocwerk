// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.LookOver;
using Blocwerk.Core.Services;
using Blocwerk.Core.Tests.LookOver;
using Blocwerk.Web.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The web wall page's read-only cards for the companion app (activity, leaderboard, temperature, look-over): members
/// read them through the page's own services, anyone else gets 404. Who may call at all is
/// <see cref="DeviceApiRoutePostureTests"/>.
/// </summary>
public sealed class DeviceWallReadsControllerTests
{
    [Fact]
    public async Task AMember_ReadsTheWallAndBoulderActivity_NewestFirst_Paged()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var boulder = await VisitFixture.SeedBoulderAsync(h, h.WallId);
        var at = new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
        await AddAsync(
            h,
            new ActivityLogEntry { WallId = h.WallId, UserId = h.Owner.Id, Type = ActivityType.WallPhotoUploaded, Timestamp = at },
            new ActivityLogEntry { WallId = h.WallId, BoulderId = boulder, UserId = h.Owner.Id, Type = ActivityType.BoulderCreated, Details = "Crimp", Timestamp = at.AddHours(1) });
        h.ActingUser = await h.AddMemberAsync("member@test", WallRole.Member);
        var activity = new ActivityLogService(h.DbContextFactory, h.CurrentUser);

        var wall = Page(await Controller(h).GetActivity(h.WallId, activity, page: 0, pageSize: 1));
        Assert.Equal((2, 1), (wall.TotalCount, wall.Items.Count));
        Assert.Equal(("BoulderCreated", "Crimp", h.Owner.Id), (wall.Items[0].Type, wall.Items[0].Details, wall.Items[0].UserId));

        var ofBoulder = Page(await Controller(h).GetBoulderActivity(h.WallId, boulder, activity, h.BoulderService));
        Assert.Equal(boulder, Assert.Single(ofBoulder.Items).BoulderId);
        Assert.IsType<NotFoundResult>(await Controller(h).GetBoulderActivity(Guid.NewGuid(), boulder, activity, h.BoulderService));
    }

    [Fact]
    public async Task AMember_ReadsTheLeaderboard_AndTheLookOverState()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        h.ActingUser = await h.AddMemberAsync("member@test", WallRole.Member);
        var progression = new ProgressionService(h.DbContextFactory, h.CurrentUser, h.WallService, NullLogger<ProgressionService>.Instance);

        var board = Assert.IsType<List<DeviceLeaderboardEntry>>(Assert.IsType<OkObjectResult>(await Controller(h).GetLeaderboard(h.WallId, progression, default)).Value);
        Assert.Contains(board, e => e.UserId == h.ActingUser.Id);

        var state = Assert.IsType<WallLookOverState>(Assert.IsType<OkObjectResult>(await Controller(h).GetLookOver(h.WallId, LookOverFixture.Service(h), default)).Value);
        Assert.False(state.Pending);
        Assert.False(state.CanLookOver);
    }

    [Fact]
    public async Task TheTemperature_IsTheLatestReadingAndTheWindowsAverages()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var now = DateTimeOffset.UtcNow;
        await AddAsync(
            h,
            new WallTemperatureReading { WallId = h.WallId, TemperatureCelsius = 18, RecordedAt = now.AddHours(-2) },
            new WallTemperatureReading { WallId = h.WallId, TemperatureCelsius = 20, RecordedAt = now.AddMinutes(-1) });

        var day = Temperature(await Controller(h).GetTemperature(h.WallId, h.WallTemperatureService, "24h"));
        Assert.Equal(20, day.LatestCelsius);
        Assert.Equal(2, day.Buckets.Sum(b => b.SampleCount));
        Assert.Equal(TimeSpan.FromHours(24).TotalSeconds / 120, day.BucketSeconds, 3);

        var all = Temperature(await Controller(h).GetTemperature(h.WallId, h.WallTemperatureService, "all"));
        Assert.NotNull(all.From);
        Assert.IsType<ObjectResult>(await Controller(h).GetTemperature(h.WallId, h.WallTemperatureService, "1w"));
    }

    [Fact]
    public async Task AnOutsider_Gets404_OnEveryCard()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        await using (var db = h.CreateContext())
        {
            var outsider = new User { Identifier = "outsider@test", DisplayName = "Outsider" };
            db.Users.Add(outsider);
            await db.SaveChangesAsync();
            h.ActingUser = outsider;
        }

        var activity = new ActivityLogService(h.DbContextFactory, h.CurrentUser);
        var progression = new ProgressionService(h.DbContextFactory, h.CurrentUser, h.WallService, NullLogger<ProgressionService>.Instance);
        Assert.IsType<NotFoundResult>(await Controller(h).GetActivity(h.WallId, activity));
        Assert.IsType<NotFoundResult>(await Controller(h).GetLeaderboard(h.WallId, progression, default));
        Assert.IsType<NotFoundResult>(await Controller(h).GetTemperature(h.WallId, h.WallTemperatureService));
        Assert.IsType<NotFoundResult>(await Controller(h).GetLookOver(h.WallId, LookOverFixture.Service(h), default));
    }

    private static DeviceActivityPage Page(IActionResult result) =>
        Assert.IsType<DeviceActivityPage>(Assert.IsType<OkObjectResult>(result).Value);

    private static DeviceTemperatureResponse Temperature(IActionResult result) =>
        Assert.IsType<DeviceTemperatureResponse>(Assert.IsType<OkObjectResult>(result).Value);

    private static async Task AddAsync(WallTestHarness h, params object[] rows)
    {
        await using var db = h.CreateContext();
        db.AddRange(rows);
        await db.SaveChangesAsync();
    }

    private static DeviceWallReadsController Controller(WallTestHarness h) =>
        new(h.DbContextFactory, h.CurrentUser)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = DeviceKeyTestPrincipals.Device() },
            },
        };
}
