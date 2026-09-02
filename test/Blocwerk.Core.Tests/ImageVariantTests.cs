using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Configuration;
using Blocwerk.Core.Services;
using Blocwerk.Web.Endpoints;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The variant cache itself. The stored image is always the camera original — hold detection,
/// alignment and the big-wall panel matcher read it, and their accuracy depends on that resolution
/// — so what the browser gets is a rendition derived from it on demand. These pin the properties
/// that make that safe: never an upscale, never a stale rendition behind a fresh ETag, and never a
/// corrupt file when two viewers land on the same wall at once.
/// </summary>
public class ImageVariantTests
{
    [Fact]
    public async Task Variant_IsDownscaled_AndKeepsTheAspectRatio()
    {
        var original = TestImages.Noise(4000, 3000, SKEncodedImageFormat.Jpeg, 95);
        var cache = NewCache(out _);

        var variant = await cache.GetOrCreateAsync(
            Key(), 1280, () => Task.FromResult<byte[]?>(original), CancellationToken.None);

        Assert.NotNull(variant);
        Assert.False(variant.IsOriginal);
        Assert.Equal("image/jpeg", variant.ContentType);

        using var decoded = SKBitmap.Decode(variant.Bytes);
        Assert.Equal(1280, decoded.Width);
        Assert.Equal(960, decoded.Height);
        Assert.True(variant.Bytes.Length < original.Length, "a rendition must be smaller than the original");
    }

    /// <summary>
    /// A rendition wider than the source would be a blurry upscale that costs more bytes than the
    /// original. The original itself is the sharpest answer, so it is what comes back.
    /// </summary>
    [Fact]
    public async Task Variant_NeverUpscalesBeyondTheOriginal()
    {
        var original = TestImages.Noise(900, 600, SKEncodedImageFormat.Jpeg, 90);
        var cache = NewCache(out var root);

        var variant = await cache.GetOrCreateAsync(
            Key(), 2560, () => Task.FromResult<byte[]?>(original), CancellationToken.None);

        Assert.NotNull(variant);
        Assert.True(variant.IsOriginal);
        Assert.Same(original, variant.Bytes);
        Assert.Empty(Directory.GetFiles(root, "*.jpg", SearchOption.AllDirectories));
    }

    /// <summary>The second request must be served off disk instead of re-reading the blob.</summary>
    [Fact]
    public async Task Variant_IsCached_AndTheOriginalIsNotReloaded()
    {
        var original = TestImages.Noise(3000, 2000, SKEncodedImageFormat.Jpeg, 90);
        var cache = NewCache(out _);
        var key = Key();
        var loads = 0;

        Task<byte[]?> Load()
        {
            loads++;
            return Task.FromResult<byte[]?>(original);
        }

        var first = await cache.GetOrCreateAsync(key, 640, Load, CancellationToken.None);
        var second = await cache.GetOrCreateAsync(key, 640, Load, CancellationToken.None);

        Assert.Equal(1, loads);
        Assert.Equal(first!.Bytes, second!.Bytes);
    }

    /// <summary>
    /// The version half of the key is built from the same parts as the ETag, so a re-uploaded photo
    /// can never be answered out of the previous upload's renditions — and the stale ones go away.
    /// </summary>
    [Fact]
    public async Task Variant_IsRegenerated_AndSwept_WhenTheImageIsReplaced()
    {
        var first = TestImages.Noise(3000, 2000, SKEncodedImageFormat.Jpeg, 90);
        var second = TestImages.Noise(2400, 1600, SKEncodedImageFormat.Jpeg, 90);
        var cache = NewCache(out var root);
        var identity = ImageResponse.Key(Guid.NewGuid(), "live");

        var before = await cache.GetOrCreateAsync(
            new ImageVariantKey(identity, ImageResponse.Key(3, first.Length, "image/jpeg")),
            640, () => Task.FromResult<byte[]?>(first), CancellationToken.None);

        var after = await cache.GetOrCreateAsync(
            new ImageVariantKey(identity, ImageResponse.Key(4, second.Length, "image/jpeg")),
            640, () => Task.FromResult<byte[]?>(second), CancellationToken.None);

        Assert.NotEqual(before!.Bytes, after!.Bytes);
        Assert.Single(Directory.GetFiles(root, "*.jpg", SearchOption.AllDirectories));
    }

    /// <summary>
    /// Two viewers landing on the same wall at once hit the same uncached rendition. The write is a
    /// temp file plus a rename, so nobody can observe a half-written one.
    /// </summary>
    [Fact]
    public async Task Variant_SurvivesConcurrentRequestsForTheSameRendition()
    {
        var original = TestImages.Noise(3000, 2000, SKEncodedImageFormat.Jpeg, 90);
        var cache = NewCache(out _);
        var key = Key();

        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ =>
            cache.GetOrCreateAsync(key, 1280, () => Task.FromResult<byte[]?>(original), CancellationToken.None)));

        foreach (var variant in results)
        {
            Assert.NotNull(variant);
            using var decoded = SKBitmap.Decode(variant.Bytes);
            Assert.Equal(1280, decoded.Width);
        }
    }

    /// <summary>Bytes that are not a readable image must not take the request down.</summary>
    [Fact]
    public async Task Variant_ServesUndecodableBytesAsTheyAre()
    {
        var garbage = new byte[] { 1, 2, 3, 4 };
        var cache = NewCache(out _);

        var variant = await cache.GetOrCreateAsync(
            Key(), 640, () => Task.FromResult<byte[]?>(garbage), CancellationToken.None);

        Assert.NotNull(variant);
        Assert.True(variant.IsOriginal);
        Assert.Same(garbage, variant.Bytes);
    }

    /// <summary>
    /// A phone held sideways writes upright pixels plus an EXIF orientation tag, and browsers
    /// rotate the ORIGINAL according to it. SKBitmap.Decode does not, and a JPEG re-encode drops the
    /// tag — so a rendition that ignored it would appear rotated the instant the page upgraded to
    /// it. The rotation is baked into the rendition instead, which is what makes the swap invisible.
    /// </summary>
    [Fact]
    public async Task Variant_HonoursExifOrientation_SoTheSwapDoesNotRotateTheImage()
    {
        // 4000x3000 pixels tagged "rotate 90" — displayed, that is 3000 wide by 4000 tall.
        var original = TestImages.WithExifOrientation(TestImages.Noise(4000, 3000), 6);
        var cache = NewCache(out _);

        var variant = await cache.GetOrCreateAsync(
            Key(), 1280, () => Task.FromResult<byte[]?>(original), CancellationToken.None);

        Assert.NotNull(variant);
        using var decoded = SKBitmap.Decode(variant.Bytes);
        Assert.Equal(1280, decoded.Width);

        // Portrait, matching how the browser lays the original out — not the 960 a naive
        // width-driven resize of the raw 4000x3000 pixel grid would produce.
        Assert.Equal(1707, decoded.Height);
        Assert.True(decoded.Height > decoded.Width, "an orientation-6 photo must render portrait");
    }

    /// <summary>
    /// The width the ladder is compared against is the DISPLAYED width, so a sideways photo that is
    /// narrow once rotated must not be re-rendered wider than it really is.
    /// </summary>
    [Fact]
    public async Task Variant_MeasuresTheNoUpscaleRule_AgainstTheOrientedWidth()
    {
        // 2000x1000 raw, tagged "rotate 90" => displayed 1000 wide. 1280 would be an upscale.
        var original = TestImages.WithExifOrientation(TestImages.Noise(2000, 1000), 6);
        var cache = NewCache(out _);

        var variant = await cache.GetOrCreateAsync(
            Key(), 1280, () => Task.FromResult<byte[]?>(original), CancellationToken.None);

        Assert.NotNull(variant);
        Assert.True(variant.IsOriginal, "1280 is wider than the 1000 px the browser displays this at");
        Assert.Same(original, variant.Bytes);
    }

    /// <summary>
    /// The image stitcher uploads PNGs off a canvas it never background-fills, so they carry real
    /// alpha. JPEG has no alpha channel and would flatten every transparent pixel to black, so a
    /// transparent source is kept in a format that has one.
    /// </summary>
    [Fact]
    public async Task Variant_KeepsTransparency_InsteadOfFlatteningItToBlack()
    {
        var original = TestImages.TransparentPng(2000, 1000);
        var cache = NewCache(out _);

        var variant = await cache.GetOrCreateAsync(
            Key(), 640, () => Task.FromResult<byte[]?>(original), CancellationToken.None);

        Assert.NotNull(variant);
        Assert.False(variant.IsOriginal);
        Assert.Equal("image/webp", variant.ContentType);

        using var decoded = SKBitmap.Decode(variant.Bytes);
        Assert.Equal(640, decoded.Width);

        // The right half of the fixture is transparent. Black would mean the alpha was flattened.
        var transparent = decoded.GetPixel(decoded.Width - 8, decoded.Height / 2);
        Assert.True(transparent.Alpha < 32, $"expected transparency, got {transparent}");
    }

    /// <summary>An opaque photo stays JPEG — WebP is for the sources that actually need it.</summary>
    [Fact]
    public async Task Variant_StaysJpeg_ForAnOpaquePhoto()
    {
        var cache = NewCache(out _);

        var variant = await cache.GetOrCreateAsync(
            Key(), 640, () => Task.FromResult<byte[]?>(TestImages.Noise(2000, 1500)), CancellationToken.None);

        Assert.Equal("image/jpeg", variant!.ContentType);
    }

    /// <summary>
    /// Detection, alignment and the overlap matcher must read the stored ORIGINAL and nothing else.
    /// The matcher's gates are absolute pixel distances tuned at full camera resolution
    /// (OpenCvHoldOverlapMatcher's GatePx/GeoScale, and NeighbourConsistency dividing by a hardcoded
    /// 4032), so feeding it a downscaled rendition would silently change what counts as a match.
    /// This pins the separation structurally: the variant cache is a WEB concern, and no domain
    /// service may take a dependency on it — a compile-time-invisible mistake that a future
    /// "just resize it first" refactor could otherwise make without anyone noticing.
    /// </summary>
    [Fact]
    public void VariantCache_IsNotReachable_FromAnyDomainService()
    {
        var core = typeof(IImageVariantCache).Assembly;

        var consumers = core.GetTypes()
            .Where(t => t != typeof(FileSystemImageVariantCache))
            .SelectMany(t => t.GetConstructors().Select(c => new { Type = t, Ctor = c }))
            .Where(x => x.Ctor.GetParameters().Any(p => p.ParameterType == typeof(IImageVariantCache)))
            .Select(x => x.Type.FullName)
            .ToList();

        var offenders = string.Join(", ", consumers);
        Assert.True(consumers.Count == 0, $"no service in Blocwerk.Core may depend on the variant cache — detection and matching must see the original. Offenders: {offenders}");
    }

    private static ImageVariantKey Key() =>
        new(ImageResponse.Key(Guid.NewGuid(), "live"), ImageResponse.Key(1, 4096, "image/jpeg"));

    /// <summary>A cache rooted in a fresh temp directory, so no test can see another's renditions.</summary>
    private static FileSystemImageVariantCache NewCache(out string variantRoot)
    {
        var storage = Path.Combine(Path.GetTempPath(), "bwk-variant-tests", Guid.NewGuid().ToString("N"));
        var settings = new BlocwerkSettings();
        settings.WallImage.StoragePath = storage;
        variantRoot = Path.Combine(storage, "variants");
        return new FileSystemImageVariantCache(settings, NullLogger<FileSystemImageVariantCache>.Instance);
    }
}
