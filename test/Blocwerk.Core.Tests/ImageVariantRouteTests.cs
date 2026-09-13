using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Configuration;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Services;
using Blocwerk.Web.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SkiaSharp;

namespace Blocwerk.Core.Tests;

/// <summary>
/// End-to-end over the real wall-photo route: the actual registered endpoint, its actual parameter
/// binding, its actual DI resolution, driven by a real request. Unit tests of the cache prove it
/// can resize; only this proves anything ever ASKS it to — that <c>?w=</c> is genuinely wired from
/// the query string through to resized bytes in the response body, rather than a validator and a
/// cache sitting unreachable behind an endpoint that never reads the parameter.
/// </summary>
/// <remarks>
/// The current-generation <c>/photo</c> is served from the wall's CENTER <see cref="WallPanel"/>
/// (grid slot 0,0), not the retiring <c>Wall.Photo</c>, so every test seeds that panel with the
/// bytes under test. A real SQLite-backed context resolves the centre panel id and gates wall
/// access exactly as production does; only the panel byte/tag service is substituted, so the
/// variant cache, the renderer and the response plumbing remain the production ones.
/// </remarks>
public class ImageVariantRouteTests : IDisposable
{
    private const string PhotoRoute = "/api/walls/{wallId:guid}/photo";

    /// <summary>
    /// The version token the seeded centre panel carries. The panel is created at the wall's current
    /// generation (0), exactly as the upload path and the startup converge do, and the panel tag's
    /// live version IS that generation — so a substituted tag must report the same value for the
    /// cache key (and the warmer) to line up with the real one.
    /// </summary>
    private const long CenterPanelGeneration = 0;

    private readonly WallTestHarness harness = new();

    public void Dispose() => harness.Dispose();

    [Fact]
    public async Task Route_WithAnAllowedWidth_ServesResizedBytes_AndFillsTheCache()
    {
        var original = TestImages.Noise(4000, 3000);
        var (invoke, root) = await RouteAsync(original);

        var http = Request("?w=640");
        await invoke(http);

        Assert.Equal(StatusCodes.Status200OK, http.Response.StatusCode);
        Assert.Equal("image/jpeg", http.Response.ContentType);

        using var decoded = SKBitmap.Decode(Body(http));
        Assert.Equal(640, decoded.Width);
        Assert.Equal(480, decoded.Height);
        Assert.True(Body(http).Length < original.Length / 4, "a 640 px rendition of a 12 MP photo must be far smaller");

        // The rendition was not merely computed — it landed on disk, where the next request finds it.
        Assert.Single(Directory.GetFiles(Path.Combine(root, "variants"), "*-640.*", SearchOption.AllDirectories));
    }

    /// <summary>
    /// Every rung of the ladder has to actually render, not just pass validation. A width the
    /// server advertises but cannot serve would be a broken image on exactly the devices that ask
    /// for it.
    /// </summary>
    [Theory]
    [InlineData(640)]
    [InlineData(1280)]
    [InlineData(1920)]
    [InlineData(2560)]
    public async Task Route_ServesEveryAdvertisedWidth(int width)
    {
        var original = TestImages.Noise(4000, 3000);
        var (invoke, _) = await RouteAsync(original);

        var http = Request($"?w={width}");
        await invoke(http);

        Assert.Equal(StatusCodes.Status200OK, http.Response.StatusCode);
        using var decoded = SKBitmap.Decode(Body(http));
        Assert.Equal(width, decoded.Width);
    }

    /// <summary>The original is what detection reads, and what an untouched URL must still return.</summary>
    [Fact]
    public async Task Route_WithoutAWidth_ServesTheStoredOriginalByteForByte()
    {
        var original = TestImages.Noise(4000, 3000);
        var (invoke, _) = await RouteAsync(original);

        var http = Request(string.Empty);
        await invoke(http);

        Assert.Equal(StatusCodes.Status200OK, http.Response.StatusCode);
        Assert.Equal(original, Body(http));
    }

    [Theory]
    [InlineData("?w=641")]
    [InlineData("?w=0")]
    [InlineData("?w=-1")]
    [InlineData("?w=100000")]
    public async Task Route_RefusesAWidthThatIsNotOnTheLadder(string query)
    {
        var original = TestImages.Noise(1200, 900);
        var (invoke, root) = await RouteAsync(original);

        var http = Request(query);
        await invoke(http);

        Assert.Equal(StatusCodes.Status404NotFound, http.Response.StatusCode);
        Assert.False(Directory.Exists(Path.Combine(root, "variants", "x")));
    }

    /// <summary>A rendition must carry its own validator, and honour a matching one.</summary>
    [Fact]
    public async Task Route_RevalidatesARenditionWith304()
    {
        var original = TestImages.Noise(4000, 3000);
        var (invoke, _) = await RouteAsync(original);

        var first = Request("?w=1280");
        await invoke(first);
        var etag = first.Response.Headers.ETag.ToString();
        Assert.False(string.IsNullOrEmpty(etag));

        var second = Request("?w=1280");
        second.Request.Headers.IfNoneMatch = etag;
        await invoke(second);

        Assert.Equal(StatusCodes.Status304NotModified, second.Response.StatusCode);

        // ...and the 640 rendition's tag must NOT satisfy the 1280 one.
        var other = Request("?w=640");
        other.Request.Headers.IfNoneMatch = etag;
        await invoke(other);
        Assert.Equal(StatusCodes.Status200OK, other.Response.StatusCode);
    }

    /// <summary>
    /// The real endpoint, resolved out of the real route table, as a callable delegate. The centre
    /// (0,0) panel carrying <paramref name="photo"/> is seeded into a real SQLite context so the
    /// route resolves it and gates wall access for real; only the panel byte/tag service is
    /// substituted — the variant cache, the renderer and the response plumbing are the production
    /// ones.
    /// </summary>
    private async Task<(Func<HttpContext, Task> Invoke, string StorageRoot)> RouteAsync(byte[] photo)
    {
        await SeedCenterPanelAsync(photo);

        var panelService = Substitute.For<IWallPanelService>();
        panelService.GetPanelPhotoTagAsync(harness.WallId, Arg.Any<Guid>())
            .Returns(new WallPhotoTag(photo.Length, "image/jpeg", CenterPanelGeneration, IsArchived: false));
        panelService.GetPanelPhotoAsync(harness.WallId, Arg.Any<Guid>())
            .Returns(new WallPhoto(photo, "image/jpeg"));

        var storage = Path.Combine(Path.GetTempPath(), "bwk-variant-route", Guid.NewGuid().ToString("N"));
        var settings = new BlocwerkSettings();
        settings.WallImage.StoragePath = storage;

        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton(panelService);
        // The /photo route resolves IWallService for its Wall.Photo fallback (used only when a wall
        // has no live centre panel). These tests seed a centre panel, so it never runs — but DI must
        // still resolve the type.
        builder.Services.AddSingleton(Substitute.For<IWallService>());
        builder.Services.AddSingleton(harness.CurrentUser);
        builder.Services.AddSingleton<IDbContextFactory<BlocwerkDbContext>>(harness.DbContextFactory);
        builder.Services.AddSingleton(Substitute.For<IKioskContext>());
        builder.Services.AddSingleton<IImageVariantCache>(
            new FileSystemImageVariantCache(settings, NullLogger<FileSystemImageVariantCache>.Instance));

        var app = builder.Build();
        app.MapWallPhotos();

        var endpoint = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == PhotoRoute);

        var services = app.Services;
        return (http =>
        {
            http.RequestServices = services;
            return endpoint.RequestDelegate!(http);
        }, storage);
    }

    /// <summary>
    /// Seeds the wall with a live centre (0,0) panel carrying <paramref name="photo"/> — the shape
    /// the <c>/photo</c> route now serves — at the wall's current generation.
    /// </summary>
    private async Task SeedCenterPanelAsync(byte[] photo)
    {
        await harness.SeedWallAsync(holdCount: 0);

        await using var db = harness.CreateContext();
        db.WallPanels.Add(new WallPanel
        {
            WallId = harness.WallId,
            Col = 0,
            Row = 0,
            Photo = photo,
            PhotoContentType = "image/jpeg",
            Generation = (int)CenterPanelGeneration,
        });
        await db.SaveChangesAsync();
    }

    private DefaultHttpContext Request(string query)
    {
        var http = new DefaultHttpContext();
        http.Request.Method = HttpMethods.Get;
        http.Request.RouteValues["wallId"] = harness.WallId.ToString();
        http.Request.QueryString = new QueryString(query);
        http.Response.Body = new MemoryStream();
        return http;
    }

    private static byte[] Body(HttpContext http) => ((MemoryStream)http.Response.Body).ToArray();
}
