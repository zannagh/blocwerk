// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json;
using Blocwerk.Contracts.Assets;
using Blocwerk.Core.Assets;
using Blocwerk.Core.Assets.Manifest;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>The manifest's shape (design §6.4): tiers, sizes, the wall hash, null shas and de-duplication.</summary>
public class ManifestShapeTests
{
    [Fact]
    public async Task Tiers_Sizes_AndTotals_FollowTheRows()
    {
        using var f = new ManifestFixture();
        var s = await f.SeedAsync();

        var m = (await f.GetAsync())!;
        var panel = Assert.Single(m.Assets, a => a.Sha == s.Panel);

        Assert.Equal((ManifestTiers.Core, ManifestAssetKinds.Panel, s.PanelId, 0, 0, 1), (panel.Tier, panel.Kind, panel.Id, panel.Col, panel.Row, panel.Gen));
        Assert.Equal(f.Assets.PanelBytes.Length, panel.Bytes);
        Assert.Equal([640, 1280, 1920, 2560], panel.Widths);
        Assert.Equal(ManifestTiers.Lazy, Tier(m, s.WallPhoto));
        Assert.Equal(ManifestTiers.Lazy, Tier(m, s.ResetPhoto));
        Assert.Equal(ManifestTiers.Lazy, Tier(m, s.Gallery));
        Assert.NotNull(Assert.Single(m.Assets, a => a.Sha == s.Gallery).At);
        Assert.All(m.Assets.Where(a => a.Kind != ManifestAssetKinds.Gallery), a => Assert.Null(a.At));
        Assert.Equal(ManifestTiers.Lazy, Tier(m, s.VideoThumbnail));
        Assert.Equal(ManifestTiers.View3d, Tier(m, s.Texture));
        Assert.Equal("F1", Assert.Single(m.Assets, a => a.Sha == s.Mask).Facet);
        Assert.Equal([0, 40_000], m.Assets.Where(a => a.Kind == ManifestAssetKinds.Splat).Select(a => a.Splats!.Value));
        Assert.All(m.Assets.Where(a => a.Kind == ManifestAssetKinds.Splat), a => Assert.Equal(ManifestTiers.View3d, a.Tier));
        Assert.Null(Assert.Single(m.Assets, a => a.Sha == s.Texture).Widths);

        Assert.Equal(f.Assets.PanelBytes.Length, m.Totals.Core);
        Assert.Equal(3 + 3 + 4 + 3 + 3, m.Totals.Lazy);
        Assert.Equal(2 + 2 + 4 + 2, m.Totals.View3d);
        Assert.Equal([ManifestTiers.Core, ManifestTiers.Lazy, ManifestTiers.View3d], m.Assets.Select(a => a.Tier).Distinct());
        Assert.Equal((1, 0), (m.V, m.Pending));
    }

    [Fact]
    public async Task TheJson_CarriesTheDocumentedFields_AndAlwaysASha()
    {
        using var f = new ManifestFixture();
        await f.SeedAsync();
        await using (var db = f.Assets.Harness.CreateContext())
        {
            await db.WallResets.ExecuteUpdateAsync(u => u.SetProperty(r => r.PreviousPhotoSha256, (byte[]?)null));
        }

        var http = await f.GetHttpAsync();
        using var doc = JsonDocument.Parse(((MemoryStream)http.Response.Body).ToArray());
        var root = doc.RootElement;

        foreach (var name in new[] { "v", "wallId", "sha", "cursor", "generation", "pending", "totals", "assets" })
        {
            Assert.True(root.TryGetProperty(name, out _), $"missing {name}");
        }

        Assert.False(root.TryGetProperty("budget", out _), "no wall-level budget: wall data is always cached");
        Assert.True(root.GetProperty("totals").TryGetProperty("view3d", out _));
        Assert.Equal(1, root.GetProperty("pending").GetInt32());
        Assert.All(root.GetProperty("assets").EnumerateArray(), a => Assert.True(a.TryGetProperty("sha", out _)));
        var unhashed = Assert.Single(root.GetProperty("assets").EnumerateArray(), a => a.GetProperty("sha").ValueKind == JsonValueKind.Null);
        Assert.Equal(ManifestAssetKinds.ResetPhoto, unhashed.GetProperty("kind").GetString());
        Assert.False(unhashed.TryGetProperty("facet", out _));
    }

    [Fact]
    public async Task TheWallHash_IsDeterministic_AndIgnoresTheCursor()
    {
        using var f = new ManifestFixture();
        await f.SeedAsync();
        var first = (await f.GetAsync())!;
        await f.AddFeedRowAsync(Contracts.Feed.FeedKinds.WallManifest);
        var rebuilt = (await f.GetAsync())!;

        Assert.NotEqual(first.Cursor, rebuilt.Cursor);
        Assert.Equal(first.Sha, rebuilt.Sha);
        Assert.Equal(first.Sha, ManifestAssembly.WallHash(rebuilt with { Cursor = "other" }));
        Assert.NotEqual(first.Sha, ManifestAssembly.WallHash(rebuilt with { Generation = 99 }));
        Assert.Equal(first.Sha, ManifestAssembly.Assemble(f.WallId, first.Generation, first.Assets.Reverse()).Sha);
    }

    [Fact]
    public async Task AHistoricPanel_IsLazy_AndAnUncoveredOrigin_MakesTheWallPhotoCore()
    {
        using var f = new ManifestFixture();
        var s = await f.SeedAsync();
        byte[] next = [30, 30, 30];
        await using (var db = f.Assets.Harness.CreateContext())
        {
            db.WallPanels.Add(new WallPanel { WallId = f.WallId, Photo = next, Generation = 2 });
            db.WallPanels.Add(new WallPanel { WallId = f.WallId, Col = 1, Photo = [31], Generation = 1 });
            await db.SaveChangesAsync();
        }

        var m = (await f.GetAsync())!;
        Assert.Equal(ManifestTiers.Lazy, Tier(m, s.Panel));
        Assert.Equal(ManifestTiers.Core, Tier(m, AssetRouteFixture.Hex(next)));
        Assert.Equal(ManifestTiers.Lazy, Tier(m, s.WallPhoto));

        await using (var db = f.Assets.Harness.CreateContext())
        {
            await db.WallPanels.Where(p => p.Col == 0).ExecuteDeleteAsync();
        }

        f.Time.Advance(WallManifestService.MaxAge);
        Assert.Equal(ManifestTiers.Core, Tier((await f.GetAsync())!, s.WallPhoto));
    }

    /// <summary>A promote copies the centre panel's photo to the wall: one entry, the panel's.</summary>
    [Fact]
    public async Task OneSha_IsListedOnce_UnderItsStrongestTier()
    {
        using var f = new ManifestFixture();
        var s = await f.SeedAsync();
        await using (var db = f.Assets.Harness.CreateContext())
        {
            var wall = await db.Walls.IgnoreQueryFilters().SingleAsync(w => w.Id == f.WallId);
            wall.Photo = f.Assets.PanelBytes;
            await db.SaveChangesAsync();
        }

        var m = (await f.GetAsync())!;
        var entry = Assert.Single(m.Assets, a => a.Sha == s.Panel);
        Assert.Equal(ManifestAssetKinds.Panel, entry.Kind);
        Assert.DoesNotContain(m.Assets, a => a.Kind == ManifestAssetKinds.WallPhoto);
        Assert.Equal(m.Assets.Count, m.Assets.Select(a => a.Sha).Distinct().Count());
    }

    /// <summary>Unhashed rows are listed network-only, never hashed on the request path.</summary>
    [Fact]
    public async Task AnUnhashedRow_IsListedWithANullSha_AndIsNotHashedByTheRequest()
    {
        using var f = new ManifestFixture();
        var s = await f.SeedAsync();
        await using (var db = f.Assets.Harness.CreateContext())
        {
            await db.WallPanels.Where(p => p.Id == s.PanelId).ExecuteUpdateAsync(u => u.SetProperty(p => p.PhotoSha256, (byte[]?)null));
        }

        var m = (await f.GetAsync())!;
        var panel = Assert.Single(m.Assets, a => a.Id == s.PanelId);
        Assert.Null(panel.Sha);
        Assert.Equal(ManifestTiers.Core, panel.Tier);
        Assert.Equal(1, m.Pending);
        await using var check = f.Assets.Harness.CreateContext();
        Assert.Null(await check.WallPanels.Where(p => p.Id == s.PanelId).Select(p => p.PhotoSha256).SingleAsync());
        Assert.Equal(ContentHash.ToHex(ContentHash.Of(f.Assets.PanelBytes)), s.Panel);
    }

    private static string Tier(WallManifest m, string sha) => Assert.Single(m.Assets, a => a.Sha == sha).Tier;
}
