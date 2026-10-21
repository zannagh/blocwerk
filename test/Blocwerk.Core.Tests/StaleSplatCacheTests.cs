// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Security.Claims;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry.View3D;
using Blocwerk.Core.Services;
using Blocwerk.Web.Endpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The splat and texture routes are keyed by model id, while a re-run swaps the rows under it and a LOD
/// backfill adds levels to it. The 3D view therefore hands out urls naming the bytes (<c>?v=</c>): those
/// change with the bytes and alone are immutable; an old or unversioned url revalidates.
/// </summary>
public class StaleSplatCacheTests
{
    [Fact]
    public async Task ARetrain_GivesTheSplatANewUrl_AndTheOldOneIsNoLongerImmutable()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        s.SplatClient.IsConfigured = true;
        var captureId = await s.StartCaptureAsync(photos: 3);
        await s.Processor.ProcessAsync(captureId, CancellationToken.None);
        var before = (await ViewAsync(h)).SplatUrl!;
        Assert.Equal(ImageResponse.ImmutableCacheControl, await CacheControlAsync(h, s, before));

        Assert.Empty(await s.Service.RetrainPhotoRealAsync(captureId, SplatQuality.Max));
        s.SplatClient.Spz = FakeComputeJobClient.Gzip(new byte[8192]);
        await s.Processor.ProcessAsync(captureId, CancellationToken.None);

        var after = (await ViewAsync(h)).SplatUrl!;
        Assert.Equal(before.Split('?')[0], after.Split('?')[0]);    // same model, same route
        Assert.NotEqual(before, after);
        Assert.Equal(ImageResponse.MutableCacheControl, await CacheControlAsync(h, s, before));
        Assert.Equal(ImageResponse.ImmutableCacheControl, await CacheControlAsync(h, s, after));
    }

    [Fact]
    public async Task ALodBackfill_GivesTheLevelUrlsNewVersions_AndAnEarlierLevelUrlIsNoLongerImmutable()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        s.SplatClient.IsConfigured = true;
        s.SplatClient.Spz = SpzDecimatorTests.Spz(330_000, shDegree: 0);
        var captureId = await s.StartCaptureAsync();
        await s.Processor.ProcessAsync(captureId, CancellationToken.None);
        WallGeometrySplat row;
        await using (var db = h.CreateContext())
        {
            row = await db.WallGeometrySplats.SingleAsync();
            row.LodLevelsJson = null;                               // a scene stored before the ladder
            await db.SaveChangesAsync();
        }

        // Before the backfill ?lod=120000 is the full scene (the row has no such level).
        var early = WallGeometrySplats.LevelUrl(h.WallId, row, 120_000);
        Assert.Single((await ViewAsync(h)).SplatLevels);
        Assert.Equal(ImageResponse.ImmutableCacheControl, await CacheControlAsync(h, s, early));

        var backfill = new SplatLodBackfill(h.RootContextFactory, s.Files, NullLogger<SplatLodBackfill>.Instance);
        Assert.Equal(1, await backfill.RunAsync(CancellationToken.None));

        var level = (await ViewAsync(h)).SplatLevels.Single(l => l.Splats == 120_000).Url;
        Assert.Equal(early.Split("&v=")[0], level.Split("&v=")[0]);
        Assert.NotEqual(early, level);
        Assert.Equal(ImageResponse.MutableCacheControl, await CacheControlAsync(h, s, early));
        Assert.Equal(ImageResponse.ImmutableCacheControl, await CacheControlAsync(h, s, level));
    }

    [Fact]
    public async Task AnUnversionedSplatUrl_FromAnOldClient_IsServedWithRevalidation()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        s.SplatClient.IsConfigured = true;
        await s.Processor.ProcessAsync(await s.StartCaptureAsync(), CancellationToken.None);
        var url = (await ViewAsync(h)).SplatUrl!.Split('?')[0];

        Assert.Equal(ImageResponse.MutableCacheControl, await CacheControlAsync(h, s, url));
        Assert.Equal(ImageResponse.MutableCacheControl, await CacheControlAsync(h, s, url + "?v=stale"));
    }

    [Fact]
    public async Task AReTexture_GivesTheTextureANewUrl_AndTheOldOneIsNoLongerImmutable()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        await s.Processor.ProcessAsync(await s.StartCaptureAsync(), CancellationToken.None);
        var before = (await ViewAsync(h)).Textures[0].Url;
        Assert.Equal(ImageResponse.ImmutableCacheControl, await TextureCacheControlAsync(h, s, before));
        Assert.Equal(ImageResponse.MutableCacheControl, await TextureCacheControlAsync(h, s, before.Split('?')[0]));

        // What a re-texture does: the model's rows are replaced, same model and facet.
        await using (var db = h.CreateContext())
        {
            var old = await db.WallGeometryTextures.Where(t => t.FacetId == "0").SingleAsync();
            db.WallGeometryTextures.Remove(old);
            db.WallGeometryTextures.Add(new WallGeometryTexture
            {
                GeometryModelId = old.GeometryModelId, FacetId = old.FacetId, StoredPath = old.StoredPath,
                ContentType = old.ContentType, SizeBytes = old.SizeBytes, WidthPx = old.WidthPx, HeightPx = old.HeightPx,
                AMin = old.AMin, AMax = old.AMax, BMin = old.BMin, BMax = old.BMax,
            });
            await db.SaveChangesAsync();
        }

        var after = (await ViewAsync(h)).Textures[0].Url;
        Assert.NotEqual(before, after);
        Assert.Equal(ImageResponse.MutableCacheControl, await TextureCacheControlAsync(h, s, before));
        Assert.Equal(ImageResponse.ImmutableCacheControl, await TextureCacheControlAsync(h, s, after));
    }

    [Fact]
    public void EveryLinkBuilder_ChangesItsUrl_WhenTheBytesChange()
    {
        var wall = Guid.NewGuid();
        var model = Guid.NewGuid();
        var splat = new WallGeometrySplat
        {
            GeometryModelId = model, StoredPath = "a.spz", FrameJson = "{}", SizeBytes = 10, MobileStoredPath = "m.spz", MobileSizeBytes = 5,
        };
        var rerun = new WallGeometrySplat
        {
            GeometryModelId = model, StoredPath = "b.spz", FrameJson = "{}", SizeBytes = 10, MobileStoredPath = "n.spz", MobileSizeBytes = 5,
        };
        Assert.NotEqual(WallGeometrySplats.Url(wall, splat), WallGeometrySplats.Url(wall, rerun));
        Assert.NotEqual(WallGeometrySplats.Url(wall, splat, mobile: true), WallGeometrySplats.Url(wall, rerun, mobile: true));
        Assert.NotEqual(WallGeometrySplats.Url(wall, splat), WallGeometrySplats.Url(wall, splat, mobile: true));

        var before = WallGeometrySplats.LevelUrl(wall, splat, 40_000);
        splat.LodLevelsJson = SplatLodLadder.Serialize([new SplatLodLevel(40_000, "l.spz", 3)]);
        Assert.NotEqual(before, WallGeometrySplats.LevelUrl(wall, splat, 40_000));

        var texture = new WallGeometryTexture { FacetId = "0", StoredPath = "t.jpg", SizeBytes = 7, MaskSizeBytes = 2 };
        var retextured = new WallGeometryTexture { FacetId = "0", StoredPath = "t.jpg", SizeBytes = 7, MaskSizeBytes = 2 };
        Assert.NotEqual(WallCaptureService.TextureVersion(texture), WallCaptureService.TextureVersion(retextured));
        Assert.NotEqual(WallCaptureService.MaskVersion(texture), WallCaptureService.MaskVersion(retextured));
    }

    private static async Task<Wall3DView> ViewAsync(WallTestHarness h) =>
        (await new Wall3DViewService(
            h.WallService, h.CurrentUser, h.DbContextFactory, Wall3DViewAccessTests.Captures(h),
            NullLogger<Wall3DViewService>.Instance).BuildAsync(h.WallId, null)).View!;

    private static async Task<string> CacheControlAsync(WallTestHarness h, CaptureScenario s, string url)
    {
        var http = AssetRequests.For(url);
        var modelId = Guid.Parse(url.Split('?')[0].Split('/')[^2]);
        await WallGeometrySplatEndpoints.HandleAsync(
            h.WallId, modelId, null, AssetRequests.Query(url, "lod"), new ClaimsPrincipal(), http, h.WallService,
            h.CurrentUser, h.DbContextFactory, Substitute.For<IKioskContext>(), s.Files, CancellationToken.None);
        return http.Response.Headers.CacheControl.ToString();
    }

    private static async Task<string> TextureCacheControlAsync(WallTestHarness h, CaptureScenario s, string url)
    {
        var http = AssetRequests.For(url);
        var parts = url.Split('?')[0].Split('/');
        await WallGeometryTextureEndpoints.HandleAsync(
            h.WallId, Guid.Parse(parts[^3]), parts[^1], null, new ClaimsPrincipal(), http, h.WallService,
            h.CurrentUser, h.DbContextFactory, Substitute.For<IKioskContext>(), s.Files, CancellationToken.None);
        return http.Response.Headers.CacheControl.ToString();
    }
}
