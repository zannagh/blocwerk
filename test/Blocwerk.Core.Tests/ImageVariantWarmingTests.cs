using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Configuration;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Blocwerk.Web.Endpoints;
using Blocwerk.Web.Maintenance;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The cache warmer, tested for the one property that decides whether it does anything at all: a
/// rendition it wrote must be the rendition a real request goes on to find. A warmer that derived
/// its cache key even slightly differently would still report success, still fill the disk, and
/// still leave every first viewer paying for a full render.
/// </summary>
public class ImageVariantWarmingTests
{
    /// <summary>
    /// Warm, then drive the REAL wall-photo endpoint and prove the original is never read again.
    /// The stored photo is the only source a render could come from, so a request that serves
    /// resized bytes without touching it can only have served the warmed file.
    /// </summary>
    [Fact]
    public async Task AWarmedVariant_IsTheOneARealRequestHits()
    {
        using var harness = new WallTestHarness();
        var photo = TestImages.Noise(2000, 1500);
        await SeedPhotoAsync(harness, photo);

        var (cache, storageRoot) = Cache();
        var summary = await Warmer(harness, cache).WarmAsync(Log(), CancellationToken.None);
        Assert.True(summary.Generated > 0, "warming a 2000 px photo must produce renditions");
        Assert.Equal(0, summary.Failed);

        var (invoke, panelService) = Route(harness, photo, cache);

        var http = Request(harness.WallId, "?w=640");
        await invoke(http);

        Assert.Equal(StatusCodes.Status200OK, http.Response.StatusCode);
        Assert.NotEmpty(Body(http));

        // The cache answered from disk: the endpoint's loader — the only path to the stored panel
        // bytes — was never called, so nothing was re-rendered.
        await panelService.DidNotReceive().GetPanelPhotoAsync(harness.WallId, Arg.Any<Guid>());

        // And the bytes it served are the very file warming wrote.
        var warmed = Directory.GetFiles(Path.Combine(storageRoot, "variants"), "*-640.*", SearchOption.AllDirectories)
            .Select(File.ReadAllBytes)
            .ToList();
        Assert.Contains(warmed, bytes => bytes.SequenceEqual(Body(http)));
    }

    /// <summary>Re-running must be cheap: nothing generated, nothing written, nothing failed.</summary>
    [Fact]
    public async Task WarmingIsIdempotent()
    {
        using var harness = new WallTestHarness();
        await SeedPhotoAsync(harness, TestImages.Noise(2000, 1500));

        var (cache, _) = Cache();
        var warmer = Warmer(harness, cache);

        var first = await warmer.WarmAsync(Log(), CancellationToken.None);
        var second = await warmer.WarmAsync(Log(), CancellationToken.None);

        Assert.True(first.Generated > 0);
        Assert.Equal(first.Images, second.Images);
        Assert.Equal(0, second.Generated);
        Assert.Equal(0L, second.BytesWritten);
        Assert.Equal(0, second.Failed);
    }

    /// <summary>
    /// Every servable slot gets its own cache entry, because every one has its own identity: the
    /// live photo alone is addressable through three different routes.
    /// </summary>
    [Fact]
    public async Task WarmingCoversEveryRouteThatCanServeTheSameBytes()
    {
        using var harness = new WallTestHarness();
        var photo = TestImages.Noise(2000, 1500);
        await SeedPhotoAsync(harness, photo);

        var (cache, storageRoot) = Cache();
        await Warmer(harness, cache).WarmAsync(Log(), CancellationToken.None);

        var tag = new WallPhotoTag(photo.Length, "image/jpeg", 0, IsArchived: false);
        foreach (var identity in new[]
                 {
                     ImageIdentity.WallPhoto(harness.WallId),
                     ImageIdentity.WallGenerationPhoto(harness.WallId, 0),
                     ImageIdentity.LegacyGalleryImage(harness.WallId, WallGallerySource.WallPhoto, harness.WallId),
                 })
        {
            var key = ImageResponse.VariantKey(tag, identity);
            var directory = Path.Combine(storageRoot, "variants", key.Identity);
            Assert.True(Directory.Exists(directory), $"no cache directory for identity {key.Identity}");
            Assert.NotEmpty(Directory.GetFiles(directory, $"{key.Version}-640.*"));
        }
    }

    /// <summary>An image the warmer cannot read must be counted and stepped over, not fatal.</summary>
    [Fact]
    public async Task AnUnreadableImageDoesNotAbortTheRun()
    {
        using var harness = new WallTestHarness();
        await SeedPhotoAsync(harness, [1, 2, 3, 4, 5, 6, 7, 8]);

        var (cache, _) = Cache();
        var summary = await Warmer(harness, cache).WarmAsync(Log(), CancellationToken.None);

        // Undecodable bytes come back as "the original IS the answer", which is a skip, not a crash.
        Assert.True(summary.Images > 0);
        Assert.Equal(0, summary.Generated);
    }

    private static async Task SeedPhotoAsync(WallTestHarness harness, byte[] photo)
    {
        await harness.SeedWallAsync(holdCount: 0);

        await using var db = harness.CreateContext();
        var wall = db.Walls.Single(w => w.Id == harness.WallId);
        wall.Photo = photo;
        wall.PhotoContentType = "image/jpeg";

        // Every wall is now a big wall: the live (0,0) centre panel is the canonical image the
        // /photo route serves, seeded here with the same bytes at the wall's current generation
        // (which the panel tag's live version reports), exactly as the upload path does.
        db.WallPanels.Add(new WallPanel
        {
            WallId = harness.WallId,
            Col = 0,
            Row = 0,
            Photo = photo,
            PhotoContentType = "image/jpeg",
            Generation = wall.CurrentGeneration,
        });
        await db.SaveChangesAsync();
    }

    private static ImageVariantWarmer Warmer(WallTestHarness harness, IImageVariantCache cache) =>
        new(harness.DbContextFactory, harness.WallImageStorage, cache, NullLogger<ImageVariantWarmer>.Instance);

    private static MaintenanceJobLog Log() => new(_ => { }, _ => { });

    private static (IImageVariantCache Cache, string Root) Cache()
    {
        var root = Path.Combine(Path.GetTempPath(), "bwk-warm", Guid.NewGuid().ToString("N"));
        var settings = new BlocwerkSettings();
        settings.WallImage.StoragePath = root;

        return (new FileSystemImageVariantCache(settings, NullLogger<FileSystemImageVariantCache>.Instance), root);
    }

    /// <summary>
    /// The production wall-photo endpoint over the SAME cache the warmer filled. The centre panel is
    /// resolved and wall access gated through the harness's real SQLite context; only the panel
    /// byte/tag service is substituted, so a read of the stored panel bytes is observable — proving
    /// the cache, not the loader, answered.
    /// </summary>
    private static (Func<HttpContext, Task> Invoke, IWallPanelService Service) Route(
        WallTestHarness harness, byte[] photo, IImageVariantCache cache)
    {
        var panelService = Substitute.For<IWallPanelService>();
        panelService.GetPanelPhotoTagAsync(harness.WallId, Arg.Any<Guid>())
            .Returns(new WallPhotoTag(photo.Length, "image/jpeg", 0, IsArchived: false));
        panelService.GetPanelPhotoAsync(harness.WallId, Arg.Any<Guid>())
            .Returns(new WallPhoto(photo, "image/jpeg"));

        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton(panelService);
        // The /photo route resolves IWallService for its Wall.Photo fallback (unused here — a centre
        // panel is seeded — but DI must resolve the type).
        builder.Services.AddSingleton(Substitute.For<IWallService>());
        builder.Services.AddSingleton(harness.CurrentUser);
        builder.Services.AddSingleton<IDbContextFactory<BlocwerkDbContext>>(harness.DbContextFactory);
        builder.Services.AddSingleton(Substitute.For<IKioskContext>());
        builder.Services.AddSingleton(cache);

        var app = builder.Build();
        app.MapWallPhotos();

        var endpoint = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == "/api/walls/{wallId:guid}/photo");

        var services = app.Services;
        return (http =>
        {
            http.RequestServices = services;
            return endpoint.RequestDelegate!(http);
        }, panelService);
    }

    private static DefaultHttpContext Request(Guid wallId, string query)
    {
        var http = new DefaultHttpContext();
        http.Request.Method = HttpMethods.Get;
        http.Request.RouteValues["wallId"] = wallId.ToString();
        http.Request.QueryString = new QueryString(query);
        http.Response.Body = new MemoryStream();
        return http;
    }

    private static byte[] Body(HttpContext http) => ((MemoryStream)http.Response.Body).ToArray();
}
