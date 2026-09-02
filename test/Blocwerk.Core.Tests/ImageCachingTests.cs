using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Configuration;
using Blocwerk.Core.Services;
using Blocwerk.Web.Endpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The image byte routes serve multi-megabyte Postgres blobs, so the whole point of the
/// conditional-request path is that a browser which already holds an image is answered without the
/// blob ever being read. These pin that: the 304 and the HEAD must not invoke the loader.
/// <para>
/// They also pin the variant contract: the STORED image is always the camera original (hold
/// detection depends on it), the browser is offered downscaled renditions off a fixed width ladder,
/// and the width is part of the validator and of the cache key.
/// </para>
/// </summary>
public class ImageCachingTests
{
    [Fact]
    public async Task MatchingIfNoneMatch_Returns304_WithoutLoadingTheBytes()
    {
        var etag = ImageResponse.Etag(Guid.NewGuid(), "live", 3, 8_563_000);
        var http = NewContext();
        http.Request.Method = HttpMethods.Get;
        http.Request.Headers.IfNoneMatch = etag;

        var loaded = false;

        var result = await ImageResponse.ConditionalAsync(
            http, etag, "image/jpeg", 8_563_000, immutable: false, Load);

        await result.ExecuteAsync(http);

        Assert.False(loaded, "the 304 path must not read the blob out of Postgres");
        Assert.Equal(StatusCodes.Status304NotModified, http.Response.StatusCode);
        Assert.Equal(etag, http.Response.Headers.ETag);
        Assert.Equal(ImageResponse.MutableCacheControl, http.Response.Headers.CacheControl);

        Task<byte[]?> Load()
        {
            loaded = true;
            return Task.FromResult<byte[]?>([1, 2, 3]);
        }
    }

    [Fact]
    public async Task NonMatchingIfNoneMatch_ServesTheBytes_AndStillTagsThem()
    {
        var etag = ImageResponse.Etag(Guid.NewGuid(), "live", 4, 100);
        var http = NewContext();
        http.Request.Method = HttpMethods.Get;
        http.Request.Headers.IfNoneMatch = "\"something-the-browser-cached-earlier\"";
        http.Response.Body = new MemoryStream();

        var result = await ImageResponse.ConditionalAsync(
            http, etag, "image/jpeg", 3, immutable: false, () => Task.FromResult<byte[]?>([1, 2, 3]));

        await result.ExecuteAsync(http);

        Assert.Equal(StatusCodes.Status200OK, http.Response.StatusCode);
        Assert.Equal(etag, http.Response.Headers.ETag);
        Assert.Equal(3, http.Response.Body.Length);
    }

    [Fact]
    public async Task Head_IsAnsweredFromTheMetadata_WithoutLoadingTheBytes()
    {
        var etag = ImageResponse.Etag(Guid.NewGuid(), "live", 1, 4_090_000);
        var http = NewContext();
        http.Request.Method = HttpMethods.Head;

        var loaded = false;

        var result = await ImageResponse.ConditionalAsync(
            http, etag, "image/jpeg", 4_090_000, immutable: false, Load);

        await result.ExecuteAsync(http);

        Assert.False(loaded, "a HEAD must not read the blob either — the length is already known");
        Assert.Equal(4_090_000, http.Response.ContentLength);
        Assert.Equal("image/jpeg", http.Response.ContentType);

        Task<byte[]?> Load()
        {
            loaded = true;
            return Task.FromResult<byte[]?>([1, 2, 3]);
        }
    }

    /// <summary>
    /// A retired generation's photo is content-addressed by its generation number and can never be
    /// rewritten, so it is the one image the browser may hold without revalidating.
    /// </summary>
    [Fact]
    public async Task ArchivedGeneration_IsCachedWithoutRevalidation()
    {
        var http = NewContext();
        http.Request.Method = HttpMethods.Get;
        http.Response.Body = new MemoryStream();

        var result = await ImageResponse.ConditionalAsync(
            http, ImageResponse.Etag(Guid.NewGuid(), 2), "image/jpeg", 3, immutable: true,
            () => Task.FromResult<byte[]?>([1, 2, 3]));

        await result.ExecuteAsync(http);

        Assert.Equal(ImageResponse.ImmutableCacheControl, http.Response.Headers.CacheControl);
        Assert.Contains("private", http.Response.Headers.CacheControl.ToString());
    }

    /// <summary>
    /// Authenticated user content must never become cacheable in a shared proxy, whichever policy
    /// a route picks.
    /// </summary>
    [Fact]
    public void CachePolicies_AreAlwaysPrivate()
    {
        Assert.StartsWith("private", ImageResponse.MutableCacheControl, StringComparison.Ordinal);
        Assert.StartsWith("private", ImageResponse.ImmutableCacheControl, StringComparison.Ordinal);
        Assert.DoesNotContain("public", ImageResponse.MutableCacheControl, StringComparison.Ordinal);
        Assert.DoesNotContain("public", ImageResponse.ImmutableCacheControl, StringComparison.Ordinal);
    }

    [Fact]
    public void EtagChanges_WhenTheImageBehindItChanges()
    {
        var wallId = Guid.NewGuid();

        var atGeneration3 = ImageResponse.Etag(wallId, "live", 3, 8_563_000);

        Assert.NotEqual(atGeneration3, ImageResponse.Etag(wallId, "live", 4, 8_563_000));
        Assert.NotEqual(atGeneration3, ImageResponse.Etag(wallId, "live", 3, 8_563_001));
        Assert.NotEqual(atGeneration3, ImageResponse.Etag(Guid.NewGuid(), "live", 3, 8_563_000));
        Assert.Equal(atGeneration3, ImageResponse.Etag(wallId, "live", 3, 8_563_000));
    }

    /// <summary>
    /// The stored image IS the camera original — hold detection, alignment and the panel matcher
    /// all read it — so a route asked for no particular width must hand back exactly those bytes.
    /// </summary>
    [Fact]
    public async Task NoWidth_ServesTheStoredOriginalUntouched()
    {
        var original = TestImages.Noise(3000, 2000, SKEncodedImageFormat.Jpeg, 95);
        var http = NewContext();
        http.Request.Method = HttpMethods.Get;
        http.Response.Body = new MemoryStream();

        var result = await ImageResponse.ServeAsync(
            http, NewCache(out _), width: null, Tag(original), immutable: false,
            () => Task.FromResult<byte[]?>(original), Guid.NewGuid(), "live");

        await result.ExecuteAsync(http);

        Assert.Equal(original.Length, http.Response.Body.Length);
    }

    /// <summary>
    /// A browser holding the 640 px rendition must not be told it already has the 2560 px one, nor
    /// the original. The width is part of the validator, not just of the URL.
    /// </summary>
    [Fact]
    public async Task Etag_VariesByWidth()
    {
        var original = TestImages.Noise(3000, 2000, SKEncodedImageFormat.Jpeg, 90);
        var wallId = Guid.NewGuid();
        var tag = Tag(original);
        var cache = NewCache(out _);

        var tags = new List<string>();
        foreach (int? width in new int?[] { null, 640, 1280, 2560 })
        {
            var http = NewContext();
            http.Request.Method = HttpMethods.Get;
            http.Response.Body = new MemoryStream();

            var result = await ImageResponse.ServeAsync(
                http, cache, width, tag, immutable: false,
                () => Task.FromResult<byte[]?>(original), wallId, "live");
            await result.ExecuteAsync(http);

            tags.Add(http.Response.Headers.ETag.ToString());
        }

        Assert.Equal(tags.Count, tags.Distinct(StringComparer.Ordinal).Count());
        Assert.DoesNotContain(string.Empty, tags);
    }

    /// <summary>
    /// An unbounded width would be a free CPU-and-disk amplifier: one render and one cache file per
    /// integer anyone cares to send. Only the fixed ladder is renderable.
    /// </summary>
    [Fact]
    public void Width_IsRejected_UnlessItIsOnTheAllowList()
    {
        Assert.True(ImageResponse.IsRenderableWidth(null));
        foreach (var allowed in ImageVariants.Widths)
        {
            Assert.True(ImageResponse.IsRenderableWidth(allowed));
        }

        foreach (var rejected in new[] { 0, -1, 1, 639, 641, 2559, 2561, 10_000, int.MaxValue })
        {
            Assert.False(ImageResponse.IsRenderableWidth(rejected), $"{rejected} must not be renderable");
        }
    }

    private static WallPhotoTag Tag(byte[] bytes) => new(bytes.Length, "image/jpeg", 1, IsArchived: false);

    /// <summary>A cache rooted in a fresh temp directory, so no test can see another's renditions.</summary>
    private static FileSystemImageVariantCache NewCache(out string variantRoot)
    {
        var storage = Path.Combine(Path.GetTempPath(), "bwk-variant-tests", Guid.NewGuid().ToString("N"));
        var settings = new BlocwerkSettings();
        settings.WallImage.StoragePath = storage;
        variantRoot = Path.Combine(storage, "variants");
        return new FileSystemImageVariantCache(settings, NullLogger<FileSystemImageVariantCache>.Instance);
    }

    /// <summary>
    /// Executing an <see cref="IResult"/> resolves a logger factory off the request services, so a
    /// bare DefaultHttpContext is not enough.
    /// </summary>
    private static DefaultHttpContext NewContext()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        return new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
    }
}
