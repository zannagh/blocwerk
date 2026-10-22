// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json;
using Blocwerk.Core.Assets.Manifest;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Web.Endpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Blocwerk.Core.Tests;

/// <summary>
/// <c>GET /api/me/cache-summary</c>: the count behind the high-storage banner (3 or more walls with 3D data). It counts
/// only walls the viewer may fetch, with the same membership and kiosk rules as the manifest.
/// </summary>
public class CacheSummaryTests
{
    [Fact]
    public async Task AMember_CountsTheirWallsWithAnActive3DModel_Only()
    {
        using var f = new ManifestFixture();
        var s = await f.SeedAsync();
        Assert.Equal(1, await CountAsync(f));

        await AddWallAsync(f, member: true, active: true, files: true);
        await AddWallAsync(f, member: true, active: false, files: true);
        await AddWallAsync(f, member: true, active: true, files: false);
        await Add3DAsync(f, s.OtherWallId, active: true, files: true);

        Assert.Equal(2, await CountAsync(f));
    }

    [Fact]
    public async Task AnAnonymousViewer_CountsNothing_ShareLinkOrNot()
    {
        using var f = new ManifestFixture();
        await f.SeedAsync();
        f.Assets.Caller = null;

        Assert.Equal(0, await CountAsync(f));
        Assert.Empty((await Service(f).GetAsync(default)).Walls);
    }

    [Fact]
    public async Task TheWalls_AreTheMembersWalls_WithOrWithout3D_AndNeverAStrangers()
    {
        using var f = new ManifestFixture();
        var s = await f.SeedAsync();
        var plain = await AddWallAsync(f, member: true, active: false, files: false);

        var walls = (await Service(f).GetAsync(default)).Walls;

        Assert.Contains(f.WallId, walls);
        Assert.Contains(plain, walls);
        Assert.DoesNotContain(s.OtherWallId, walls);
    }

    [Fact]
    public async Task AKiosk_ListsOnlyItsBoundWall_NoneWhenItMayNotCache()
    {
        using var f = new ManifestFixture();
        var s = await f.SeedAsync();
        await AddWallAsync(f, member: true, active: true, files: true);

        f.BindKiosk();
        Assert.Equal([f.WallId], (await Service(f).GetAsync(default)).Walls);
        f.BindKiosk(s.OtherWallId);
        Assert.Equal([s.OtherWallId], (await Service(f).GetAsync(default)).Walls);
        f.BindKiosk(Guid.Empty);
        Assert.Empty((await Service(f).GetAsync(default)).Walls);
    }

    [Fact]
    public async Task AKiosk_CountsOnlyTheWallItMayCache_WhoeverItActsAs()
    {
        using var f = new ManifestFixture();
        var s = await f.SeedAsync();
        await AddWallAsync(f, member: true, active: true, files: true);

        f.BindKiosk();
        Assert.Equal(1, await CountAsync(f));
        f.Assets.Caller = f.Assets.Harness.Owner;
        Assert.Equal(1, await CountAsync(f));

        f.BindKiosk(s.OtherWallId);
        Assert.Equal(0, await CountAsync(f));
        f.BindKiosk(Guid.Empty);
        Assert.Equal(0, await CountAsync(f));
    }

    [Fact]
    public async Task TheRoute_AnswersJson_AndIsNeverStored()
    {
        using var f = new ManifestFixture();
        await f.SeedAsync();
        var http = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        http.Response.Body = new MemoryStream();

        await (await ManifestEndpoints.HandleSummaryAsync(http, Service(f), default)).ExecuteAsync(http);

        Assert.Equal("no-store", http.Response.Headers.CacheControl.ToString());
        using var body = JsonDocument.Parse(((MemoryStream)http.Response.Body).ToArray());
        Assert.Equal(1, body.RootElement.GetProperty("wallsWith3d").GetInt32());
        Assert.Equal(f.WallId, body.RootElement.GetProperty("walls")[0].GetGuid());
    }

    private static CacheSummaryService Service(ManifestFixture f) =>
        new(f.Assets.Harness.DbContextFactory, f.Assets.CurrentUser, f.Assets.Kiosk, f.Assets.KioskKeys);

    private static async Task<int> CountAsync(ManifestFixture f) => (await Service(f).GetAsync(default)).WallsWith3d;

    private static async Task<Guid> AddWallAsync(ManifestFixture f, bool member, bool active, bool files)
    {
        var wall = new Wall { Name = "W" + Guid.NewGuid().ToString("N")[..6], OwnerId = f.Assets.Harness.Owner.Id };
        await using (var db = f.Assets.Harness.CreateContext())
        {
            db.Walls.Add(wall);
            if (member)
            {
                db.WallMembers.Add(new WallMember { WallId = wall.Id, UserId = f.Assets.Harness.Owner.Id, Role = WallRole.Member });
            }

            await db.SaveChangesAsync();
        }

        await Add3DAsync(f, wall.Id, active, files);
        return wall.Id;
    }

    private static async Task Add3DAsync(ManifestFixture f, Guid wallId, bool active, bool files)
    {
        await using var db = f.Assets.Harness.CreateContext();
        var model = new WallGeometryModel { WallId = wallId, Json = "{}", Source = "test", IsActive = active };
        db.WallGeometryModels.Add(model);
        if (files)
        {
            db.WallGeometryTextures.Add(new WallGeometryTexture { GeometryModelId = model.Id, FacetId = "F1", StoredPath = "t.jpg" });
        }

        await db.SaveChangesAsync();
    }
}
