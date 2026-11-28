// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Blocwerk.Web.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>
/// <c>PATCH /api/v1/me/progression</c>, <c>PATCH /api/v1/me/activities/{id}</c> and <c>GET /api/v1/me/days/{date}</c>:
/// the Activity page's edits, saved on the caller only, and any day's summary.
/// </summary>
public sealed class MeActivityControllerTests
{
    [Fact]
    public async Task TheProgressionPreference_IsSavedOnTheUser_AndAnswered()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);

        var ok = Assert.IsType<OkObjectResult>(await Controller(h, h.Owner).SetProgressionPreference(
            new ProgressionPreferenceRequest { WindowDays = 120, GroupBy = "month" }));

        var progression = Assert.IsType<ProgressionResponse>(ok.Value);
        Assert.Equal((120, "Month"), (progression.WindowDays, progression.GroupBy));
        await using var db = h.CreateContext();
        var stored = await db.Users.SingleAsync(u => u.Id == h.Owner.Id);
        Assert.Equal((120, ProgressionGroupBy.Month), (stored.ProgressionWindowDays, stored.ProgressionGroupBy));
    }

    [Theory]
    [InlineData(6, null)]
    [InlineData(366, null)]
    [InlineData(null, "Year")]
    [InlineData(null, "7")]
    public async Task ABadPreference_IsA400(int? days, string? groupBy)
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);

        Assert.IsType<BadRequestObjectResult>(await Controller(h, h.Owner).SetProgressionPreference(
            new ProgressionPreferenceRequest { WindowDays = days, GroupBy = groupBy }));
    }

    [Fact]
    public async Task TheDuration_ChangesTheCallersActivityOnly()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var mine = await AddActivityAsync(h, h.Owner.Id);
        var other = await MeProfileControllerTests.AddUserAsync(h, "other@test");
        var theirs = await AddActivityAsync(h, other.Id);
        var controller = Controller(h, h.Owner);

        var ok = Assert.IsType<OkObjectResult>(await controller.SetDuration(mine, new ActivityDurationRequest { DurationMinutes = 95 }));
        Assert.Equal(95, Assert.IsType<ActivityDetailResponse>(ok.Value).DurationMinutes);
        Assert.IsType<NotFoundObjectResult>(await controller.SetDuration(theirs, new ActivityDurationRequest { DurationMinutes = 95 }));
        Assert.IsType<BadRequestObjectResult>(await controller.SetDuration(mine, new ActivityDurationRequest { DurationMinutes = 1441 }));
        Assert.IsType<BadRequestObjectResult>(await controller.SetDuration(mine, new ActivityDurationRequest { DurationMinutes = -1 }));

        await using var db = h.CreateContext();
        Assert.Null((await db.Activities.SingleAsync(a => a.Id == theirs)).DurationMinutes);
    }

    [Fact]
    public async Task ADayLongBeforeTheWindow_IsSummarised()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        await using (var db = h.CreateContext())
        {
            db.PullupSessions.Add(new PullupSession
            {
                UserId = h.Owner.Id,
                Repetitions = 8,
                Sets = 2,
                Timestamp = new DateTimeOffset(2024, 3, 9, 18, 0, 0, TimeSpan.Zero),
            });
            await db.SaveChangesAsync();
        }

        var ok = Assert.IsType<OkObjectResult>(await Controller(h, h.Owner).GetDay("2024-03-09"));

        var day = Assert.IsType<DaySummaryResponse>(ok.Value);
        Assert.Equal(new DateOnly(2024, 3, 9), day.Date);
        Assert.Single(day.Pullups);
        Assert.IsType<BadRequestObjectResult>(await Controller(h, h.Owner).GetDay("09.03.2024"));
    }

    private static async Task<Guid> AddActivityAsync(WallTestHarness h, Guid userId)
    {
        await using var db = h.CreateContext();
        var activity = new Activity { UserId = userId, StartedAt = DateTimeOffset.UtcNow.AddHours(-2), LastEventAt = DateTimeOffset.UtcNow.AddHours(-1) };
        db.Activities.Add(activity);
        await db.SaveChangesAsync();
        return activity.Id;
    }

    private static MeActivityController Controller(WallTestHarness h, User user)
    {
        var currentUser = MeProfileControllerTests.RealCurrentUser(h, user);
        var progression = new ProgressionService(h.DbContextFactory, currentUser, h.WallService, NullLogger<ProgressionService>.Instance);
        return new MeActivityController(progression)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = DeviceKeyTestPrincipals.Device() } },
        };
    }
}
