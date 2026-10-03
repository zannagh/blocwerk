// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry.View3D;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The 3D view reads the wall through <see cref="IWallService"/> exactly as the wall
/// page does, so it must answer "who may see it" identically: members by id, share-link holders by
/// token (and only for that token's wall), nobody else — and update mode hides it from everyone but
/// the updating admin. Runs against the real query filter (SQLite harness).
/// </summary>
public class Wall3DViewAccessTests
{
    [Fact]
    public async Task Member_GetsTheView_WithPlacedHolds()
    {
        using var harness = new WallTestHarness();
        await SeedAsync(harness, withGeometry: true);

        var result = await NewService(harness).BuildAsync(harness.WallId, null);

        Assert.Equal(Wall3DViewStatus.Ok, result.Status);
        Assert.Equal("Test Wall", result.WallName);
        Assert.Single(result.View!.Holds);
        Assert.Equal(1, result.View.UnplacedHoldCount);
        Assert.True(await NewService(harness).HasActiveGeometryAsync(harness.WallId));
    }

    [Fact]
    public async Task NonMember_IsNotFound()
    {
        using var harness = new WallTestHarness();
        await SeedAsync(harness, withGeometry: true);
        harness.ActingUser = new User { Identifier = "stranger@test", DisplayName = "Stranger" };

        var result = await NewService(harness).BuildAsync(harness.WallId, null);

        Assert.Equal(Wall3DViewStatus.NotFound, result.Status);
        Assert.Null(result.View);
    }

    [Fact]
    public async Task Anonymous_WithoutShareToken_Throws_SoThePageCanSendThemToSignIn()
    {
        using var harness = new WallTestHarness();
        await SeedAsync(harness, withGeometry: true);
        harness.CurrentUser.GetCurrentUserAsync().Returns<Task<User>>(_ => throw new UnauthorizedAccessException());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => NewService(harness).BuildAsync(harness.WallId, null));
    }

    [Fact]
    public async Task ShareToken_OpensItsOwnWallOnly()
    {
        using var harness = new WallTestHarness();
        await SeedAsync(harness, withGeometry: true);
        harness.CurrentUser.GetCurrentUserAsync().Returns<Task<User>>(_ => throw new UnauthorizedAccessException());
        var service = NewService(harness);

        Assert.Equal(Wall3DViewStatus.Ok, (await service.BuildAsync(harness.WallId, null, "tok-3d")).Status);
        Assert.Equal(Wall3DViewStatus.NotFound, (await service.BuildAsync(Guid.NewGuid(), null, "tok-3d")).Status);
        Assert.Equal(Wall3DViewStatus.NotFound, (await service.BuildAsync(harness.WallId, null, "wrong")).Status);
    }

    [Fact]
    public async Task UpdateMode_HidesTheWall_FromEveryoneButTheUpdatingAdmin()
    {
        using var harness = new WallTestHarness();
        await SeedAsync(harness, withGeometry: true);
        await using (var db = harness.CreateContext())
        {
            var wall = await db.Walls.SingleAsync(w => w.Id == harness.WallId);
            wall.UnderMaintenance = true;
            wall.MaintenanceByUserId = Guid.NewGuid();
            await db.SaveChangesAsync();
        }

        var result = await NewService(harness).BuildAsync(harness.WallId, null);

        Assert.Equal(Wall3DViewStatus.UnderMaintenance, result.Status);
        Assert.Null(result.View);
    }

    [Fact]
    public async Task WallWithoutActiveModel_ReportsNoGeometry()
    {
        using var harness = new WallTestHarness();
        await SeedAsync(harness, withGeometry: false);

        var service = NewService(harness);

        Assert.Equal(Wall3DViewStatus.NoGeometry, (await service.BuildAsync(harness.WallId, null)).Status);
        Assert.False(await service.HasActiveGeometryAsync(harness.WallId));
    }

    /// <summary>A real capture service (its texture list applies the same view rules); no files are read.</summary>
    internal static WallCaptureService Captures(WallTestHarness harness, IKioskContext? kiosk = null) =>
        new(harness.DbContextFactory, harness.CurrentUser, Substitute.For<ICaptureFileStore>(), new WallCaptureQueue(),
            new FakeComputeJobClientFactory(new FakeComputeJobClient()), NullLogger<WallCaptureService>.Instance, kiosk);

    private static Wall3DViewService NewService(WallTestHarness harness) =>
        new(harness.WallService, harness.CurrentUser, harness.DbContextFactory, Captures(harness), NullLogger<Wall3DViewService>.Instance);

    private static async Task SeedAsync(WallTestHarness harness, bool withGeometry)
    {
        var holds = await harness.SeedWallAsync(holdCount: 2);
        await using var db = harness.CreateContext();
        var wall = await db.Walls.SingleAsync(w => w.Id == harness.WallId);
        wall.ShareToken = "tok-3d";

        var measured = await db.Holds.SingleAsync(h => h.Id == holds[0].Id);
        measured.FacetId = "0";
        measured.PlaneAMm = 1000;
        measured.PlaneBMm = 1500;

        if (withGeometry)
        {
            // An inactive older model must never be picked up.
            db.WallGeometryModels.Add(new WallGeometryModel
            {
                WallId = harness.WallId, Json = "{ not json", Source = "old", IsActive = false,
            });
            db.WallGeometryModels.Add(new WallGeometryModel
            {
                WallId = harness.WallId, Json = Wall3DViewBuilderTests.SolvedJson, SchemaVersion = 1,
                Source = "glyph-solver v1", IsActive = true,
            });
        }

        await db.SaveChangesAsync();
    }
}
