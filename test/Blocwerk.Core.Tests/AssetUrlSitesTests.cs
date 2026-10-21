// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Assets;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Services;
using Blocwerk.Core.Services.HybridZoom;
using Blocwerk.Web.Components.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The data the URL sites are built from: every projection that feeds <see cref="AssetUrls"/> reads its hash from the
/// database, so a hashed row renders an <c>/a/</c> URL the route serves and an unhashed row (not yet backfilled) the
/// legacy one. The hash is cleared with a raw update here, exactly the state a row is in before the 2.2 backfill.
/// </summary>
public class AssetUrlSitesTests
{
    [Fact]
    public async Task ThePanelList_CarriesTheLiveHash_AndItsUrlServesThoseBytes()
    {
        using var f = new AssetRouteFixture();
        var s = await f.SeedAsync();

        var panel = (await Panels(f).GetPanelsAsync(f.Harness.WallId)).Single(p => p.Id == s.PanelId);
        var historic = (await Panels(f).GetPanelsAsync(f.Harness.WallId, 5)).Single(p => p.Id == s.PanelId);
        var url = AssetUrls.Panel(f.Harness.WallId, panel);

        Assert.Equal(s.Panel, panel.PhotoSha);
        Assert.Equal(s.Panel, historic.PhotoSha);
        Assert.Equal($"/api/walls/{f.Harness.WallId}/a/{s.Panel}", url);
        var http = await f.GetAsync(f.Harness.WallId, url.Split('/')[^1]);
        Assert.Equal(StatusCodes.Status200OK, http.Response.StatusCode);
        Assert.Equal(f.PanelBytes, AssetRouteFixture.Body(http));
    }

    [Fact]
    public async Task AnUnhashedPanel_RendersTheLegacyRoute()
    {
        using var f = new AssetRouteFixture();
        var s = await f.SeedAsync();
        await ClearPanelHashAsync(f, s.PanelId);

        var panel = (await Panels(f).GetPanelsAsync(f.Harness.WallId)).Single(p => p.Id == s.PanelId);

        Assert.Null(panel.PhotoSha);
        Assert.Equal($"/api/walls/{f.Harness.WallId}/panels/{s.PanelId}/photo", AssetUrls.Panel(f.Harness.WallId, panel));
        Assert.DoesNotContain(s.PanelId, (await Reader(f).PanelPhotosAsync(f.Harness.WallId)).Keys);
    }

    [Fact]
    public async Task GalleryItems_CarryTheirHash_UnlessTheRowIsUnhashed()
    {
        using var f = new AssetRouteFixture();
        var s = await f.SeedAsync();
        var gallery = await f.Harness.WallImageService.GetGalleryAsync(f.Harness.WallId);

        Assert.Equal(s.Gallery, gallery.Single(i => i.Source == Core.Enums.WallGallerySource.Uploaded).Sha);
        Assert.Equal(s.WallPhoto, gallery.Single(i => i.Source == Core.Enums.WallGallerySource.WallPhoto).Sha);
        Assert.Equal(s.ResetPhoto, gallery.Single(i => i.Source == Core.Enums.WallGallerySource.ResetPhoto).Sha);

        await using (var db = f.Harness.CreateContext())
        {
            await db.WallImages.ExecuteUpdateAsync(u => u.SetProperty(i => i.Sha256, (byte[]?)null));
        }

        var item = (await f.Harness.WallImageService.GetGalleryAsync(f.Harness.WallId)).Single(i => i.Source == Core.Enums.WallGallerySource.Uploaded);
        Assert.Null(item.Sha);
        Assert.StartsWith("/media/walls/", Web.Endpoints.ResponsiveImage.Gallery(f.Harness.WallId, "Uploaded", item.Id, item.SizeBytes, null, true, item.Sha));
    }

    [Fact]
    public async Task BetaVideos_CarryTheirWallAndThumbnailHash()
    {
        using var f = new AssetRouteFixture();
        var s = await f.SeedAsync();
        Guid boulderId;
        await using (var db = f.Harness.CreateContext())
        {
            boulderId = await db.BetaVideos.Where(v => !v.Boulder.IsDraft).Select(v => v.BoulderId).SingleAsync();
        }

        var video = Assert.Single(await f.Harness.BetaVideoService.GetVideosAsync(boulderId));

        Assert.Equal(f.Harness.WallId, video.WallId);
        Assert.Equal(s.VideoThumbnail, video.ThumbnailSha);
        Assert.Equal($"/api/walls/{f.Harness.WallId}/a/{s.VideoThumbnail}", AssetUrls.Hashed(video.WallId, video.ThumbnailSha));
    }

    [Fact]
    public async Task TheWallPhotoHash_IsTheNewestCentrePanels_ElseTheRetiringWallPhoto()
    {
        using var f = new AssetRouteFixture();
        var s = await f.SeedAsync();
        var reader = Reader(f);

        Assert.Equal(s.Panel, await reader.WallPhotoAsync(f.Harness.WallId));
        Assert.Equal(s.OtherWallPanel, (await reader.WallPhotosAsync([f.Harness.WallId, s.OtherWallId]))[s.OtherWallId]);

        await using (var db = f.Harness.CreateContext())
        {
            await db.WallPanels.Where(p => p.Id == s.PanelId).ExecuteUpdateAsync(u => u.SetProperty(p => p.Col, 1));
        }

        Assert.Equal(s.WallPhoto, await reader.WallPhotoAsync(f.Harness.WallId));
    }

    /// <summary>
    /// The privacy rewrite moves a photo's hash by ExecuteUpdate, which a circuit's cached Wall never sees. The reader
    /// goes to the database every time, so the URL follows the new hash while a cached entity would still hold the old.
    /// </summary>
    [Fact]
    public async Task AHashMovedBehindACachedWall_IsWhatTheReaderReturns()
    {
        using var f = new AssetRouteFixture();
        var s = await f.SeedAsync();
        await using (var db = f.Harness.CreateContext())
        {
            await db.WallPanels.Where(p => p.WallId == f.Harness.WallId).ExecuteUpdateAsync(u => u.SetProperty(p => p.Col, 1));
        }

        Core.Entities.Wall cached;
        await using (var db = f.Harness.CreateContext())
        {
            cached = await db.Walls.AsNoTracking().SingleAsync(w => w.Id == f.Harness.WallId);
        }

        byte[] rewritten = [1, 2, 3, 4];
        await using (var db = f.Harness.CreateContext())
        {
            await db.Walls.Where(w => w.Id == f.Harness.WallId).ExecuteUpdateAsync(u => u
                .SetProperty(w => w.Photo, rewritten).SetProperty(w => w.PhotoSha256, ContentHash.Of(rewritten)));
        }

        var fresh = await Reader(f).WallPhotoAsync(f.Harness.WallId);
        Assert.Equal(s.WallPhoto, ContentHash.ToHex(cached.PhotoSha256));
        Assert.Equal(AssetRouteFixture.Hex(rewritten), fresh);
        Assert.Equal(StatusCodes.Status200OK, (await f.GetAsync(f.Harness.WallId, fresh!)).Response.StatusCode);
        AssetRouteAccessTests.AssertRefused(await f.GetAsync(f.Harness.WallId, s.WallPhoto));
    }

    [Fact]
    public async Task TheGenerationPhotoHash_IsWhatItsRouteServes()
    {
        using var f = new AssetRouteFixture();
        var s = await f.SeedAsync();
        await using (var db = f.Harness.CreateContext())
        {
            await db.Walls.Where(w => w.Id == f.Harness.WallId).ExecuteUpdateAsync(u => u.SetProperty(w => w.CurrentGeneration, 1));
        }

        Assert.Equal(s.ResetPhoto, await Reader(f).WallPhotoAtGenerationAsync(f.Harness.WallId, null, 0));
        Assert.Equal(s.WallPhoto, await Reader(f).WallPhotoAtGenerationAsync(f.Harness.WallId, null, 1));
    }

    [Fact]
    public async Task ThePanelViewer_RendersHashedPanelsUnderAsset_AndUnhashedOnesUnderTheirRoute()
    {
        var wall = Guid.NewGuid();
        var sha = AssetRouteFixture.Hex([1]);
        var hashed = new WallPanelInfo(Guid.NewGuid(), 0, 0, true, false, 1, false, PhotoSha: sha);
        var legacy = hashed with { PhotoSha = null, PhotoRevision = 3 };

        var hashedHtml = await RenderViewerAsync(wall, hashed);
        var legacyHtml = await RenderViewerAsync(wall, legacy);

        Assert.Contains($"src=\"/api/walls/{wall}/a/{sha}/1280?token=t\"", hashedHtml);
        Assert.Contains($"src=\"/api/walls/{wall}/panels/{hashed.Id}/photo?token=t&amp;rev=3&amp;w=1280\"", legacyHtml);
    }

    [Fact]
    public async Task ThePanelGrid_RendersHashedPanelsUnderAsset_AndUnhashedOnesUnderTheirRoute()
    {
        var wall = Guid.NewGuid();
        var sha = AssetRouteFixture.Hex([2]);
        var hashed = new WallPanelInfo(Guid.NewGuid(), 0, 0, true, false, 1, false, PhotoSha: sha);
        var legacy = new WallPanelInfo(Guid.NewGuid(), 1, 0, true, false, 1, false);
        var panels = Substitute.For<IWallPanelService>();
        panels.GetPanelsAsync(wall).Returns([hashed, legacy]);
        panels.GetFrontierPositionsAsync(wall).Returns([]);
        var glyphs = Substitute.For<IWallGlyphService>();
        glyphs.GetGlyphSettingsAsync(wall).Returns(new WallGlyphSettings(false, null));

        var html = await RenderAsync<WallPanelGrid>(
            new Dictionary<string, object?> { ["WallId"] = wall },
            services => services.AddSingleton(panels).AddSingleton(glyphs));

        Assert.Contains($"src=\"/api/walls/{wall}/a/{sha}/640\"", html);
        Assert.Contains($"src=\"/api/walls/{wall}/panels/{legacy.Id}/photo?w=640\"", html);
    }

    internal static WallPanelService Panels(AssetRouteFixture f) => new(
        f.Harness.DbContextFactory, f.Harness.CurrentUser, f.Harness.HoldDetection, Substitute.For<IHoldOverlapMatcher>(),
        NullLogger<WallPanelService>.Instance);

    private static AssetHashReader Reader(AssetRouteFixture f) => new(f.Harness.DbContextFactory, f.Harness.WallService);

    private static async Task ClearPanelHashAsync(AssetRouteFixture f, Guid panelId)
    {
        await using var db = f.Harness.CreateContext();
        await db.WallPanels.Where(p => p.Id == panelId).ExecuteUpdateAsync(u => u.SetProperty(p => p.PhotoSha256, (byte[]?)null));
    }

    private static Task<string> RenderViewerAsync(Guid wall, WallPanelInfo panel) => RenderAsync<MultiPanelViewer>(
        new Dictionary<string, object?>
        {
            ["WallId"] = wall,
            ["ShareToken"] = "t",
            ["Panels"] = (IReadOnlyList<WallPanelInfo>)[panel],
            ["HoldsByPanel"] = (IReadOnlyDictionary<Guid, IReadOnlyList<PanelHold>>)new Dictionary<Guid, IReadOnlyList<PanelHold>>(),
        },
        services => services.AddSingleton(Substitute.For<IJSRuntime>()).AddSingleton(new HybridZoomOptions()));

    private static async Task<string> RenderAsync<TComponent>(Dictionary<string, object?> parameters, Action<IServiceCollection> configure)
        where TComponent : IComponent
    {
        var collection = new ServiceCollection().AddLogging();
        configure(collection);
        await using var services = collection.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, NullLoggerFactory.Instance);
        return await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<TComponent>(ParameterView.FromDictionary(parameters))).ToHtmlString());
    }
}
