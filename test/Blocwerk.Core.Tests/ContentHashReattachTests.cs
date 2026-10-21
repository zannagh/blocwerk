// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Security.Cryptography;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Rows that reach the save in a state other than plain Added or plainly edited, none of which may keep the hash it held
/// in memory: a detached row with new bytes re-attached through a navigation (tracked as Modified while the save
/// detects), and an attached Unchanged row whose bytes are set afterwards (found by the stamp's own scan).
/// </summary>
public class ContentHashReattachTests
{
    [Fact]
    public Task ADetachedPanelWithANewPhoto_ReattachedThroughANavigation_IsHashedAgain() => ReattachedAsync(new WallTestHarness());

    [PostgresFact]
    [Trait("Db", "Postgres")]
    public Task ADetachedPanelWithANewPhoto_ReattachedThroughANavigation_IsHashedAgain_OnPostgres() =>
        ReattachedAsync(new WallTestHarness(PostgresTestDatabase.Create()));

    [Fact]
    public Task AnAttachedPanel_WhosePhotoIsSetAfterwards_IsHashedAgain() => AttachedAsync(new WallTestHarness());

    [PostgresFact]
    [Trait("Db", "Postgres")]
    public Task AnAttachedPanel_WhosePhotoIsSetAfterwards_IsHashedAgain_OnPostgres() =>
        AttachedAsync(new WallTestHarness(PostgresTestDatabase.Create()));

    private static async Task ReattachedAsync(WallTestHarness harness)
    {
        using var h = harness;
        await h.SeedWallAsync(holdCount: 0);
        var id = await AddPanelAsync(h);
        var detached = await LoadDetachedAsync(h, id);
        detached.Photo = [2, 2, 2];

        EntityState state;
        await using (var db = h.CreateContext())
        {
            var wall = await db.Walls.IgnoreQueryFilters().Include(w => w.Panels).SingleAsync(w => w.Id == h.WallId);
            var loaded = wall.Panels.Single(p => p.Id == id);
            wall.Panels.Remove(loaded);
            db.Entry(loaded).State = EntityState.Detached;
            wall.Panels.Add(detached);
            await db.SaveChangesAsync();
            state = db.Entry(detached).State;
        }

        Assert.Equal(EntityState.Unchanged, state);
        await AssertHashedAsync(h, id, [2, 2, 2]);
    }

    private static async Task AttachedAsync(WallTestHarness harness)
    {
        using var h = harness;
        await h.SeedWallAsync(holdCount: 0);
        var id = await AddPanelAsync(h);
        var detached = await LoadDetachedAsync(h, id);
        await using (var db = h.CreateContext())
        {
            db.WallPanels.Attach(detached);
            detached.Photo = [3, 3, 3];
            await db.SaveChangesAsync();
        }

        await AssertHashedAsync(h, id, [3, 3, 3]);
    }

    private static async Task<Guid> AddPanelAsync(WallTestHarness h)
    {
        await using var db = h.CreateContext();
        var panel = new WallPanel { WallId = h.WallId, Col = 7, Photo = [1, 1] };
        db.WallPanels.Add(panel);
        await db.SaveChangesAsync();
        return panel.Id;
    }

    private static async Task<WallPanel> LoadDetachedAsync(WallTestHarness h, Guid id)
    {
        await using var db = h.CreateContext();
        return await db.WallPanels.AsNoTracking().SingleAsync(p => p.Id == id);
    }

    private static async Task AssertHashedAsync(WallTestHarness h, Guid id, byte[] photo)
    {
        await using var check = h.CreateContext();
        var row = await check.WallPanels.AsNoTracking().SingleAsync(p => p.Id == id);
        Assert.Equal(photo, row.Photo);
        Assert.Equal(SHA256.HashData(photo), row.PhotoSha256);
    }
}
