// <copyright file="PanelPhotoInfoCacheTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Configuration;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry.View3D;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// <see cref="PanelPhotoInfoLoader"/> through <see cref="Wall3DViewCache"/>: a cached photo's header is not read from the
/// database again, and a replaced photo (promote, re-upload) has a new <see cref="PhotoInfoStamp"/> and so is read anew.
/// </summary>
public class PanelPhotoInfoCacheTests
{
    [Fact]
    public async Task CachedPhoto_IsNotReadAgain()
    {
        using var harness = new WallTestHarness();
        await harness.SeedWallAsync(holdCount: 0);
        var panelId = await SeedPanelAsync(harness, Photo(1008, 756, 14));
        using var cache = new Wall3DViewCache(new Wall3DViewCacheSettings());
        var counter = new PhotoHeaderQueryCounter();
        var key = new Wall3DPhotoKey(panelId, 0);

        var first = await LoadAsync(harness, counter, cache, key);
        var second = await LoadAsync(harness, counter, cache, key);

        Assert.Equal((1008, 756), (first[key].Width, first[key].Height));
        Assert.Equal(first[key], second[key]);
        Assert.Equal(1, counter.Count);
    }

    [Fact]
    public async Task ReplacedPhoto_IsANewKey()
    {
        using var harness = new WallTestHarness();
        await harness.SeedWallAsync(holdCount: 0);
        var panelId = await SeedPanelAsync(harness, Photo(1008, 756, 14));
        using var cache = new Wall3DViewCache(new Wall3DViewCacheSettings());
        var counter = new PhotoHeaderQueryCounter();
        var key = new Wall3DPhotoKey(panelId, 0);
        Assert.Equal(1008, (await LoadAsync(harness, counter, cache, key))[key].Width);

        // A new photo goes live on the same panel row and generation (as a re-upload does).
        await using (var db = harness.CreateContext())
        {
            var panel = await db.WallPanels.SingleAsync(p => p.Id == panelId);
            panel.Photo = Photo(640, 480, 26);
            await db.SaveChangesAsync();
        }

        var info = (await LoadAsync(harness, counter, cache, key))[key];
        Assert.Equal((640, 480), (info.Width, info.Height));
        Assert.Equal(26 / 36.0 * 640, info.FocalPx!.Value, 6);
        Assert.Equal(2, counter.Count);
    }

    [Fact]
    public async Task InPlaceEdit_WithSameLengthAndTail_IsANewKeyThroughPhotoRevision()
    {
        using var harness = new WallTestHarness();
        await harness.SeedWallAsync(holdCount: 0);
        var before = Photo(1008, 756, 14);
        var after = Photo(1008, 756, 26);

        // Only the header differs, so length and tail alone could not tell the edited photo from the old one.
        Assert.Equal(before.Length, after.Length);
        Assert.Equal(before[^PhotoInfoStamp.TailBytes..], after[^PhotoInfoStamp.TailBytes..]);
        var panelId = await SeedPanelAsync(harness, before);
        using var cache = new Wall3DViewCache(new Wall3DViewCacheSettings());
        var counter = new PhotoHeaderQueryCounter();
        var key = new Wall3DPhotoKey(panelId, 0);
        Assert.Equal(14 / 36.0 * 1008, (await LoadAsync(harness, counter, cache, key))[key].FocalPx!.Value, 6);

        // A crop rewrites the photo in place and bumps the revision, on the same row and generation.
        await using (var db = harness.CreateContext())
        {
            var panel = await db.WallPanels.SingleAsync(p => p.Id == panelId);
            panel.Photo = after;
            panel.PhotoRevision++;
            await db.SaveChangesAsync();
        }

        var info = (await LoadAsync(harness, counter, cache, key))[key];
        Assert.Equal(26 / 36.0 * 1008, info.FocalPx!.Value, 6);
        Assert.Equal(2, counter.Count);
    }

    [Fact]
    public async Task UncachedPanels_AreReadTogether_CachedOnesNot()
    {
        using var harness = new WallTestHarness();
        await harness.SeedWallAsync(holdCount: 0);
        var a = new Wall3DPhotoKey(await SeedPanelAsync(harness, Photo(320, 240, 14)), 0);
        var b = new Wall3DPhotoKey(await SeedPanelAsync(harness, Photo(400, 300, 14), col: 1), 0);
        using var cache = new Wall3DViewCache(new Wall3DViewCacheSettings());
        var counter = new PhotoHeaderQueryCounter();
        await LoadAsync(harness, counter, cache, a);

        var both = await LoadAsync(harness, counter, cache, a, b, new Wall3DPhotoKey(Guid.NewGuid(), 0));

        Assert.Equal((320, 400), (both[a].Width, both[b].Width));
        Assert.Equal(2, both.Count);
        Assert.Equal(2, counter.Count);
        await LoadAsync(harness, counter, cache, a, b);
        Assert.Equal(2, counter.Count);
    }

    /// <summary>A JPEG of <paramref name="width"/> × <paramref name="height"/> whose EXIF gives a 35 mm-equivalent focal length.</summary>
    private static byte[] Photo(int width, int height, double focal35) => ExifJpeg.Build(TestImages.Noise(width, height), focal35);

    private static async Task<Dictionary<Wall3DPhotoKey, Geometry.Footprints.PanelPhotoInfo>> LoadAsync(
        WallTestHarness harness, PhotoHeaderQueryCounter counter, Wall3DViewCache cache, params Wall3DPhotoKey[] keys)
    {
        await using var db = counter.Context(harness);
        return await PanelPhotoInfoLoader.LoadAsync(db, harness.WallId, keys, cache, CancellationToken.None);
    }

    private static async Task<Guid> SeedPanelAsync(WallTestHarness harness, byte[] photo, int col = 0)
    {
        await using var db = harness.CreateContext();
        var panel = new WallPanel { WallId = harness.WallId, Col = col, Photo = photo, PhotoContentType = "image/jpeg", Generation = 0 };
        db.WallPanels.Add(panel);
        await db.SaveChangesAsync();
        return panel.Id;
    }
}
