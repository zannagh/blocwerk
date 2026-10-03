// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Reflection;
using System.Text.Json;
using Blocwerk.Authentication.Authorization;
using Blocwerk.Authentication.Handlers;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Jobs;
using Blocwerk.Web.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Who sees which jobs: an administrator of the installation every wall's, a wall admin their walls' only, a member none,
/// a kiosk nothing; and the scriptable route (<c>GET /api/v1/admin/jobs</c>): a personal key, read access enough, stable
/// JSON names.
/// </summary>
public class JobProgressServiceTests
{
    [Fact]
    public async Task AnAppAdmin_SeesEveryWall_AWallAdminOnlyTheirs_AMemberNothing()
    {
        using var h = new WallTestHarness();
        var (mine, other) = await SeedTwoWallsAsync(h);

        Assert.Equal([mine], WallsOf(await Service(h).ListAsync(null, null, default)));

        await MakeAppAdminAsync(h, h.Owner.Id);
        Assert.Equal(new HashSet<Guid> { mine, other }, WallsOf(await Service(h).ListAsync(null, null, default)).ToHashSet());
        Assert.Equal([other], WallsOf(await Service(h).ListAsync(other, null, default)));

        h.ActingUser = await h.AddMemberAsync("member@test", WallRole.Member);
        Assert.Empty((await Service(h).ListAsync(null, null, default)).Jobs);
        Assert.False(await Service(h).CanWatchAsync(null, default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service(h).ListAsync(mine, null, default));
    }

    [Fact]
    public async Task AWallAdmin_IsRefusedAnotherWall_AndAKioskEverything()
    {
        using var h = new WallTestHarness();
        var (mine, other) = await SeedTwoWallsAsync(h);
        var kiosk = Substitute.For<IKioskContext>();
        kiosk.IsKiosk.Returns(true);
        kiosk.KioskWallId.Returns(mine);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service(h).ListAsync(other, null, default));
        await Assert.ThrowsAsync<Services.KioskRestrictedException>(() => Service(h, kiosk).ListAsync(mine, null, default));
        Assert.True(await Service(h).CanWatchAsync(mine, default));
        Assert.False(await Service(h, kiosk).CanWatchAsync(mine, default));
    }

    [Fact]
    public void TheRoute_IsARead_ForAPersonalKey_UnderTheApiKeySurface()
    {
        var authorize = Assert.Single(typeof(JobsController).GetCustomAttributes<AuthorizeAttribute>());
        var route = Assert.Single(typeof(JobsController).GetCustomAttributes<RouteAttribute>());

        Assert.Equal(BlocwerkPolicies.UserApiKey, authorize.Policy);
        Assert.Equal(ApiKeyAuthenticationHandler.SchemeName, authorize.AuthenticationSchemes);
        Assert.Equal("api/v1/admin/jobs", route.Template);
        Assert.True(ApiKeySurface.Covers(new PathString("/" + route.Template)));
        Assert.NotNull(typeof(JobsController).GetMethod(nameof(JobsController.List))!.GetCustomAttribute<HttpGetAttribute>());
    }

    [Fact]
    public async Task TheRoute_AnswersWithStableNames_403ForAForeignWall_And400ForABadWindow()
    {
        using var h = new WallTestHarness();
        var (mine, other) = await SeedTwoWallsAsync(h);
        var api = Api(h);

        var ok = Assert.IsType<OkObjectResult>(await api.List(null, 0, default));
        var json = JsonSerializer.SerializeToElement(ok.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var job = json.GetProperty("jobs").EnumerateArray().Single();

        Assert.Equal(0, json.GetProperty("recentHours").GetDouble());
        Assert.Equal(mine, job.GetProperty("wallId").GetGuid());
        string[] names =
        [
            "id", "kind", "state", "stage", "detail", "percent", "step", "totalSteps", "etaSeconds", "etaSource", "startedAt", "updatedAt",
            "endedAt", "lastError", "wallId", "wallName", "captureId", "gpuJobId", "runnerName", "runnerPaused", "attempts", "training", "stages",
        ];
        Assert.Equal(names, job.EnumerateObject().Select(p => p.Name));
        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(await api.List(other, null, default)).StatusCode);
        Assert.IsType<BadRequestObjectResult>(await api.List(null, -1, default));
    }

    private static JobProgressService Service(WallTestHarness h, IKioskContext? kiosk = null) =>
        new(h.DbContextFactory, h.CurrentUser, new JobProgressReader(h.RootContextFactory), kiosk);

    private static JobsController Api(WallTestHarness h) =>
        new(Service(h), NullLogger<JobsController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = ApiKeys.Personal(allowWrite: false) } },
        };

    private static List<Guid> WallsOf(JobProgressSnapshot snapshot) => snapshot.Jobs.Select(j => j.WallId).Distinct().ToList();

    /// <summary>A running capture on the owner's wall and one on a wall of somebody else.</summary>
    private static async Task<(Guid Mine, Guid Other)> SeedTwoWallsAsync(WallTestHarness h)
    {
        await h.SeedWallAsync(holdCount: 0);
        await CaptureTimelineTests.AddAsync(h, WallCaptureStatus.Solving);
        await using var db = h.CreateContext();
        var stranger = new User { Identifier = "stranger@test", DisplayName = "Stranger" };
        var wall = new Wall { Name = "Other wall", OwnerId = stranger.Id };
        db.Users.Add(stranger);
        db.Walls.Add(wall);
        db.WallCaptures.Add(new WallCapture { WallId = wall.Id, CreatedByUserId = stranger.Id, Status = WallCaptureStatus.Detecting });
        await db.SaveChangesAsync();
        return (h.WallId, wall.Id);
    }

    private static async Task MakeAppAdminAsync(WallTestHarness h, Guid userId)
    {
        await using var db = h.CreateContext();
        var user = await db.Users.FindAsync(userId);
        user!.Role = IdentityRole.Admin;
        await db.SaveChangesAsync();
    }
}
