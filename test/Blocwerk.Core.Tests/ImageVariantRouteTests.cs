using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Configuration;
using Blocwerk.Core.Services;
using Blocwerk.Web.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
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
public class ImageVariantRouteTests
{
    private const string PhotoRoute = "/api/walls/{wallId:guid}/photo";

    [Fact]
    public async Task Route_WithAnAllowedWidth_ServesResizedBytes_AndFillsTheCache()
    {
        var original = TestImages.Noise(4000, 3000);
        var (invoke, root) = Route(original, out _);

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
        var (invoke, _) = Route(original, out _);

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
        var (invoke, _) = Route(original, out _);

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
        var (invoke, root) = Route(original, out _);

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
        var (invoke, _) = Route(original, out _);

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

    private static readonly Guid WallId = Guid.NewGuid();

    /// <summary>
    /// The real endpoint, resolved out of the real route table, as a callable delegate. Only the
    /// wall service is substituted — the variant cache, the renderer and the response plumbing are
    /// the production ones.
    /// </summary>
    private static (Func<HttpContext, Task> Invoke, string StorageRoot) Route(byte[] photo, out IWallService service)
    {
        var wallService = Substitute.For<IWallService>();
        wallService.GetPhotoTagAsync(WallId, Arg.Any<string?>())
            .Returns(new WallPhotoTag(photo.Length, "image/jpeg", 3, IsArchived: false));
        wallService.GetPhotoAsync(WallId).Returns(photo);
        service = wallService;

        var storage = Path.Combine(Path.GetTempPath(), "bwk-variant-route", Guid.NewGuid().ToString("N"));
        var settings = new BlocwerkSettings();
        settings.WallImage.StoragePath = storage;

        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton(wallService);
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

    private static DefaultHttpContext Request(string query)
    {
        var http = new DefaultHttpContext();
        http.Request.Method = HttpMethods.Get;
        http.Request.RouteValues["wallId"] = WallId.ToString();
        http.Request.QueryString = new QueryString(query);
        http.Response.Body = new MemoryStream();
        return http;
    }

    private static byte[] Body(HttpContext http) => ((MemoryStream)http.Response.Body).ToArray();
}
