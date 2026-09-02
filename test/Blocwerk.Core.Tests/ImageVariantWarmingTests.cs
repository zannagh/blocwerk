using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Configuration;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Blocwerk.Web.Endpoints;
using Blocwerk.Web.Maintenance;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
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

        var (invoke, wallService) = Route(harness.WallId, photo, cache);

        var http = Request(harness.WallId, "?w=640");
        await invoke(http);

        Assert.Equal(StatusCodes.Status200OK, http.Response.StatusCode);
        Assert.NotEmpty(Body(http));

        // The cache answered from disk: the endpoint's loader — the only path to the stored
        // original — was never called, so nothing was re-rendered.
        await wallService.DidNotReceive().GetPhotoAsync(harness.WallId);

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
    /// The production wall-photo endpoint over the SAME cache the warmer filled, with a substituted
    /// wall service so a read of the stored original is observable.
    /// </summary>
    private static (Func<HttpContext, Task> Invoke, IWallService Service) Route(
        Guid wallId, byte[] photo, IImageVariantCache cache)
    {
        var wallService = Substitute.For<IWallService>();
        wallService.GetPhotoTagAsync(wallId, Arg.Any<string?>())
            .Returns(new WallPhotoTag(photo.Length, "image/jpeg", 0, IsArchived: false));
        wallService.GetPhotoAsync(wallId).Returns(photo);

        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton(wallService);
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
        }, wallService);
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
