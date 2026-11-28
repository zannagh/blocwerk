// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Feed;
using Blocwerk.Core.Services;
using Blocwerk.Core.Services.DeviceWrites;
using Blocwerk.Web.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>Builders and assertions shared by the companion app's setter route tests.</summary>
internal static class DeviceSetterFixture
{
    /// <summary>How ASP.NET Core binds a JSON body: camelCase, case-insensitive.</summary>
    public static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public static DeviceBouldersController Controller(WallTestHarness h) => Controller(h, h.BoulderService);

    /// <summary>The production feed interceptor on the service's contexts, so a save writes its feed rows as it does live.</summary>
    public static DeviceBouldersController FeedController(WallTestHarness h)
    {
        var feed = new FeedInterceptor(
            new FeedMapperRegistry(FeedMappers.Create()), new RecordingFeedSignal(), NullLogger<FeedInterceptor>.Instance, TestFeedCommits.Unobserved());
        var factory = new InterceptedDbContextFactory(h.DbContextFactory.ConnectionString, [feed]);
        return Controller(h, new BoulderService(factory, h.CurrentUser, h.ActivityLog, NullLogger<BoulderService>.Instance));
    }

    public static CreateBoulderRequest Create(IReadOnlyList<Hold> holds, Guid? key = null, Action<CreateBoulderRequest>? change = null)
    {
        var request = new CreateBoulderRequest
        {
            ClientRequestId = key ?? Guid.NewGuid(),
            Name = "Crimp line",
            Grade = "6B+",
            Holds = holds.Take(2).Select(hold => new BoulderHoldDto { HoldId = hold.Id }).ToList(),
        };
        change?.Invoke(request);
        return request;
    }

    /// <summary>A revise body as the app sends it: JSON, with whatever it leaves out simply absent.</summary>
    public static DeviceReviseBoulderRequest Json(string json) =>
        JsonSerializer.Deserialize<DeviceReviseBoulderRequest>(json, Web)!;

    public static string Key() => Guid.NewGuid().ToString();

    public static Guid BoulderId(OfflineActionResponse body) =>
        (Guid)body.Result!.GetType().GetProperty("boulderId")!.GetValue(body.Result)!;

    public static OfflineActionResponse Applied(IActionResult result)
    {
        var body = Assert.IsType<OfflineActionResponse>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.True(body.Applied);
        return body;
    }

    public static void Refused(IActionResult result, int status, string? message = null)
    {
        var refused = Assert.IsType<ObjectResult>(result);
        Assert.Equal(status, refused.StatusCode);
        var error = Assert.IsType<OfflineActionError>(refused.Value);
        Assert.True(error.Permanent);
        if (message is not null)
        {
            Assert.Equal(message, error.Message);
        }
    }

    /// <summary>A wall of <paramref name="owner"/> with one hold; the harness owner is a member only when <paramref name="ownerIsMember"/>.</summary>
    public static async Task<(Guid WallId, Hold Hold)> OtherWallAsync(WallTestHarness h, User owner, bool ownerIsMember)
    {
        await using var db = h.CreateContext();
        if (owner.Id != h.Owner.Id && !db.Users.Any(u => u.Id == owner.Id))
        {
            db.Users.Add(owner);
        }

        var wall = new Wall { Name = "Other", OwnerId = owner.Id };
        var hold = new Hold { WallId = wall.Id, X = 0.5, Y = 0.5, Radius = 0.02 };
        db.Walls.Add(wall);
        db.Holds.Add(hold);
        db.WallMembers.Add(new WallMember { WallId = wall.Id, UserId = owner.Id, Role = WallRole.Admin });
        if (ownerIsMember && owner.Id != h.Owner.Id)
        {
            db.WallMembers.Add(new WallMember { WallId = wall.Id, UserId = h.Owner.Id, Role = WallRole.Admin });
        }

        await db.SaveChangesAsync();
        return (wall.Id, hold);
    }

    public static async Task<User> OutsiderAsync(WallTestHarness h)
    {
        await using var db = h.CreateContext();
        var user = new User { Identifier = "outsider@test", DisplayName = "Outsider" };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private static DeviceBouldersController Controller(WallTestHarness h, IBoulderService service) =>
        new(service, h.CurrentUser, new DeviceWriteReceiptService(h.DbContextFactory))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = DeviceKeyTestPrincipals.Device() },
            },
        };
}
