// <copyright file="MarkerPlanServiceTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.MarkerPlanning;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests.MarkerPlanning;

/// <summary>
/// Saving and reading plans through a real provider: history with one current plan, the same gate as
/// the glyph settings (admin only, never a kiosk), and reads for anyone who can see the wall.
/// </summary>
public class MarkerPlanServiceTests
{
    [Fact]
    public async Task Owner_SavesAPlan_AndAMemberReadsItBack()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);

        var result = await Service(h).SavePlanAsync(h.WallId, AtticMarkerPlan.Plan);

        Assert.True(result.Saved);
        Assert.NotEmpty(result.Issues);
        h.ActingUser = await h.AddMemberAsync("climber@test", WallRole.Member);
        var read = await Service(h).GetPlanAsync(h.WallId);
        Assert.NotNull(read);
        Assert.Equal(AtticMarkerPlan.Plan.Markers, read.Markers);
    }

    [Fact]
    public async Task SavingAgain_KeepsHistory_WithExactlyOneCurrentPlan()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var service = Service(h);
        var suggested = MarkerGenerator.Generate(AtticMarkerPlan.Blank, MarkerGenerationOptions.Default);

        for (var i = 0; i < 3; i++)
        {
            Assert.True((await service.SavePlanAsync(h.WallId, i % 2 == 0 ? AtticMarkerPlan.Plan : suggested)).Saved);
        }

        await using var db = h.CreateContext();
        var rows = await db.WallMarkerPlans.Where(p => p.WallId == h.WallId).ToListAsync();
        Assert.Equal(3, rows.Count);
        Assert.Single(rows, r => r.IsCurrent);
        Assert.All(rows, r => Assert.Equal(1, r.SchemaVersion));
        Assert.Equal(AtticMarkerPlan.Plan.Markers, (await service.GetPlanAsync(h.WallId))!.Markers);
    }

    [Fact]
    public async Task APlanWithErrors_IsNotSaved()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);

        var result = await Service(h).SavePlanAsync(h.WallId, AtticMarkerPlan.Blank);

        Assert.False(result.Saved);
        Assert.Contains(result.Issues, i => i.Code == "segment-few-markers");
        Assert.Null(await Service(h).GetPlanAsync(h.WallId));
    }

    [Theory]
    [InlineData(WallRole.Member)]
    [InlineData(WallRole.Moderator)]
    public async Task NonAdmin_CannotSave(WallRole role)
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        h.ActingUser = await h.AddMemberAsync("climber@test", role);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service(h).SavePlanAsync(h.WallId, AtticMarkerPlan.Plan));

        await using var db = h.CreateContext();
        Assert.False(await db.WallMarkerPlans.AnyAsync());
    }

    [Fact]
    public async Task Kiosk_CannotSave_EvenOnItsOwnWall()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var kiosk = Substitute.For<IKioskContext>();
        kiosk.IsKiosk.Returns(true);
        kiosk.KioskWallId.Returns(h.WallId);
        var service = new MarkerPlanService(h.DbContextFactory, h.CurrentUser, NullLogger<MarkerPlanService>.Instance, kiosk);

        await Assert.ThrowsAsync<KioskRestrictedException>(() => service.SavePlanAsync(h.WallId, AtticMarkerPlan.Plan));
    }

    [Fact]
    public async Task Stranger_CannotReadTheWallsPlan()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        await Service(h).SavePlanAsync(h.WallId, AtticMarkerPlan.Plan);
        var stranger = new User { Identifier = "stranger@test", DisplayName = "Stranger" };
        await using (var db = h.CreateContext())
        {
            db.Users.Add(stranger);
            await db.SaveChangesAsync();
        }

        h.ActingUser = stranger;

        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(h).GetPlanAsync(h.WallId));
    }

    [Fact]
    public async Task DeletingTheWall_CascadesToItsPlans()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        await Service(h).SavePlanAsync(h.WallId, AtticMarkerPlan.Plan);

        await using var db = h.CreateContext();
        db.CurrentUserId = Guid.Empty;
        db.Walls.Remove(await db.Walls.SingleAsync(w => w.Id == h.WallId));
        await db.SaveChangesAsync();

        Assert.False(await db.WallMarkerPlans.AnyAsync());
    }

    [Fact]
    public async Task BuildFromMeasuredGeometry_UsesTheActiveModel_OrReturnsNullWithoutOne()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        Assert.Null(await Service(h).BuildFromMeasuredGeometryAsync(h.WallId, AtticMarkerPlan.Photo));

        await using (var db = h.CreateContext())
        {
            db.WallGeometryModels.Add(new WallGeometryModel
            {
                WallId = h.WallId,
                Json = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "MarkerPlanning", "attic-wall-geometry.json")),
                SchemaVersion = 1,
                Source = "test",
                IsActive = true,
            });
            await db.SaveChangesAsync();
        }

        var plan = await Service(h).BuildFromMeasuredGeometryAsync(h.WallId, AtticMarkerPlan.Photo);

        Assert.NotNull(plan);
        Assert.Equal([0, 1, 2, 5], plan.Segments.Select(s => s.Index));
        Assert.Equal(AtticMarkerPlan.Photo, plan.Photo);
    }

    [Fact]
    public async Task BuildFromMeasuredGeometry_IsForAdminsOnly()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        h.ActingUser = await h.AddMemberAsync("climber@test", WallRole.Member);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service(h).BuildFromMeasuredGeometryAsync(h.WallId, AtticMarkerPlan.Photo));
    }

    private static MarkerPlanService Service(WallTestHarness h) =>
        new(h.DbContextFactory, h.CurrentUser, NullLogger<MarkerPlanService>.Instance);
}
