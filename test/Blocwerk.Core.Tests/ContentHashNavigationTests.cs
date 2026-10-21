// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Security.Cryptography;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The stamp detects changes on the stamped types only, so a new hashed row that only the save's own detection finds,
/// reachable through a navigation of an already tracked row alone, is stamped as it is discovered: through a stamped
/// owner's collection, an unstamped owner's collection, and an unstamped row's reference. (Only a row with an unset key
/// can arrive that way: EF takes a discovered row whose Guid key is already set for an existing one, so these leave the
/// key empty and let EF generate it.)
/// </summary>
public class ContentHashNavigationTests
{
    private static readonly byte[] Photo = [6, 6, 6];

    [Fact]
    public async Task APanelAddedOnlyThroughTheTrackedWallsCollection_IsStamped()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var panel = new WallPanel { Id = Guid.Empty, WallId = h.WallId, Col = 3, Photo = Photo };
        await using (var db = h.CreateContext())
        {
            var wall = await db.Walls.IgnoreQueryFilters().Include(w => w.Panels).SingleAsync(w => w.Id == h.WallId);
            wall.Panels.Add(panel);
            await db.SaveChangesAsync();
        }

        Assert.Equal(SHA256.HashData(Photo), await PanelHashAsync(h, panel.Id));
    }

    [Fact]
    public async Task APanelAddedOnlyThroughAHoldsReference_IsStamped()
    {
        using var h = new WallTestHarness();
        var holdId = (await h.SeedWallAsync(holdCount: 1))[0].Id;
        var panel = new WallPanel { Id = Guid.Empty, WallId = h.WallId, Col = 4, Photo = Photo };
        await using (var db = h.CreateContext())
        {
            var hold = await db.Holds.IgnoreQueryFilters().SingleAsync(x => x.Id == holdId);
            hold.WallPanel = panel;
            await db.SaveChangesAsync();
        }

        Assert.Equal(SHA256.HashData(Photo), await PanelHashAsync(h, panel.Id));
    }

    [Fact]
    public async Task AThumbnailAddedOnlyThroughABouldersCollection_IsStamped()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var boulder = new Boulder { WallId = h.WallId, Name = "Nav", CreatedByUserId = h.Owner.Id };
        await using (var db = h.CreateContext())
        {
            db.Boulders.Add(boulder);
            await db.SaveChangesAsync();
        }

        var video = new BetaVideo { Id = Guid.Empty, BoulderId = boulder.Id, UploadedByUserId = h.Owner.Id, ContentType = "video/mp4", Thumbnail = Photo };
        await using (var db = h.CreateContext())
        {
            var tracked = await db.Boulders.IgnoreQueryFilters().Include(b => b.BetaVideos).SingleAsync(b => b.Id == boulder.Id);
            tracked.BetaVideos.Add(video);
            await db.SaveChangesAsync();
        }

        await using var check = h.CreateContext();
        Assert.Equal(
            SHA256.HashData(Photo),
            await check.BetaVideos.IgnoreQueryFilters().Where(v => v.Id == video.Id).Select(v => v.ThumbnailSha256).SingleAsync());
    }

    private static async Task<byte[]?> PanelHashAsync(WallTestHarness h, Guid id)
    {
        await using var db = h.CreateContext();
        return await db.WallPanels.IgnoreQueryFilters().Where(p => p.Id == id).Select(p => p.PhotoSha256).SingleAsync();
    }
}
