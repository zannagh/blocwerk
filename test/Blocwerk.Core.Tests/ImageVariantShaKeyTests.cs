// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Assets;
using Blocwerk.Core.Configuration;
using Blocwerk.Core.Services;
using Blocwerk.Web.Endpoints;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The variant re-key: a hashed source's renditions live under <c>(sha, width)</c>, shared by every route serving
/// those bytes; a rendition cached under the old ETag-parts key is moved over on first use (dual-read), so a deploy
/// costs no re-render; and a rendition is only cached under a sha its original really hashes to.
/// </summary>
public class ImageVariantShaKeyTests
{
    private static readonly byte[] Original = TestImages.Noise(1800, 900, SKEncodedImageFormat.Jpeg, 85);

    [Fact]
    public async Task ALegacyRendition_IsMovedToTheShaKey_OnceTheOriginalHashesToIt()
    {
        var cache = NewCache(out var root);
        var legacy = new ImageVariantKey(ImageResponse.Key(Guid.NewGuid(), "live"), ImageResponse.Key(1, Original.Length, "image/jpeg"));
        var rendered = await cache.GetOrCreateAsync(legacy, 640, Load(Original), default);
        File.WriteAllBytes(Directory.GetFiles(Path.Combine(root, legacy.Identity)).Single(), [42, 42]); // a marker no render makes

        var reads = 0;
        var hashed = await cache.GetOrCreateAsync(legacy with { Sha = Sha(Original) }, 640, Counting(() => reads++), default);

        Assert.Equal(1, reads);
        Assert.Equal([42, 42], hashed!.Bytes);
        Assert.NotNull(rendered);
        Assert.Empty(Directory.GetFiles(Path.Combine(root, legacy.Identity)));
        Assert.Single(Directory.GetFiles(Path.Combine(root, "sha"), "*-640.jpg", SearchOption.AllDirectories));
    }

    /// <summary>Release fix W3: a legacy rendition of bytes that no longer hash to the key is never moved under it.</summary>
    [Fact]
    public async Task ALegacyRendition_OfOtherBytes_IsNotAdopted()
    {
        var cache = NewCache(out var root);
        var legacy = new ImageVariantKey(ImageResponse.Key(Guid.NewGuid(), "live"), ImageResponse.Key(1, Original.Length, "image/jpeg"));
        await cache.GetOrCreateAsync(legacy, 640, Load(Original), default);

        await cache.GetOrCreateAsync(legacy with { Sha = Sha([9, 9, 9]) }, 640, Load(Original), default);

        Assert.Single(Directory.GetFiles(Path.Combine(root, legacy.Identity)));
        Assert.False(Directory.Exists(Path.Combine(root, "sha")) && Directory.GetFiles(Path.Combine(root, "sha"), "*", SearchOption.AllDirectories).Length > 0);
    }

    /// <summary>Release fix W1a: a wall photo cached under another route's key (an alternate) is found and adopted.</summary>
    [Fact]
    public async Task AnAlternateLegacyKey_IsAdoptedToo()
    {
        var cache = NewCache(out var root);
        var gallery = new ImageVariantKey(ImageResponse.Key(Guid.NewGuid(), "gallery"), ImageResponse.Key(1, Original.Length, "image/jpeg"));
        await cache.GetOrCreateAsync(gallery, 640, Load(Original), default);
        File.WriteAllBytes(Directory.GetFiles(Path.Combine(root, gallery.Identity)).Single(), [7, 7]);

        var key = new ImageVariantKey(ImageResponse.Key(Guid.NewGuid(), "photo"), ImageResponse.Key(1), Sha(Original), [(gallery.Identity, gallery.Version)]);
        var hashed = await cache.GetOrCreateAsync(key, 640, Load(Original), default, originalVerified: true);

        Assert.Equal([7, 7], hashed!.Bytes);
    }

    [Fact]
    public async Task ARendition_IsSharedByEveryIdentityServingTheSameBytes()
    {
        var cache = NewCache(out _);
        var sha = Sha(Original);
        await cache.GetOrCreateAsync(new ImageVariantKey(ImageResponse.Key("a"), ImageResponse.Key(1), sha), 1280, Load(Original), default);

        var reads = 0;
        var second = await cache.GetOrCreateAsync(new ImageVariantKey(string.Empty, string.Empty, sha), 1280, Counting(() => reads++), default);

        Assert.Equal(0, reads);
        Assert.False(second!.IsOriginal);
    }

    /// <summary>A photo rewritten between the tag read and the byte read must not be cached under the old hash.</summary>
    [Fact]
    public async Task BytesThatDoNotHashToTheKey_AreServedButNeverCached()
    {
        var cache = NewCache(out var root);
        var key = new ImageVariantKey(string.Empty, string.Empty, Sha([1, 2, 3]));

        var variant = await cache.GetOrCreateAsync(key, 640, Load(Original), default);

        Assert.NotNull(variant);
        Assert.False(Directory.Exists(Path.Combine(root, "sha")) && Directory.GetFiles(Path.Combine(root, "sha"), "*", SearchOption.AllDirectories).Length > 0);
    }

    [Fact]
    public async Task AnUnhashedSource_KeepsTheLegacyLayout()
    {
        var cache = NewCache(out var root);
        var key = new ImageVariantKey(ImageResponse.Key(Guid.NewGuid()), ImageResponse.Key(2));

        await cache.GetOrCreateAsync(key, 640, Load(Original), default);

        Assert.Single(Directory.GetFiles(Path.Combine(root, key.Identity)));
        Assert.False(Directory.Exists(Path.Combine(root, "sha")));
    }

    /// <summary>The legacy routes key by the sha their tag read from the row, so they fill the slots the hashed route uses.</summary>
    [Fact]
    public async Task ThePhotoTag_CarriesTheRowsHash_IntoTheVariantKey()
    {
        using var harness = new WallTestHarness();
        await harness.SeedWallAsync(holdCount: 0);

        var tag = await harness.WallService.GetPhotoTagAsync(harness.WallId, null);
        var key = ImageResponse.VariantKey(tag!, ImageIdentity.WallPhoto(harness.WallId));

        Assert.Equal(Sha([1, 2, 3]), tag!.Sha);
        Assert.Equal(tag.Sha, key.Sha);
        Assert.Null(ImageResponse.VariantKey(tag with { Sha = null }, ImageIdentity.WallPhoto(harness.WallId)).Sha);
    }

    private static string Sha(byte[] bytes) => ContentHash.ToHex(ContentHash.Of(bytes))!;

    private static Func<Task<byte[]?>> Load(byte[] bytes) => () => Task.FromResult<byte[]?>(bytes);

    private static Func<Task<byte[]?>> Counting(Action onRead) => () =>
    {
        onRead();
        return Task.FromResult<byte[]?>(Original);
    };

    private static FileSystemImageVariantCache NewCache(out string variantRoot)
    {
        var storage = Path.Combine(Path.GetTempPath(), "bwk-variant-sha-tests", Guid.NewGuid().ToString("N"));
        var settings = new BlocwerkSettings();
        settings.WallImage.StoragePath = storage;
        variantRoot = Path.Combine(storage, "variants");
        return new FileSystemImageVariantCache(settings, NullLogger<FileSystemImageVariantCache>.Instance);
    }
}
