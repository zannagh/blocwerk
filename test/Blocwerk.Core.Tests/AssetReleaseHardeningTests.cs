// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Assets;
using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Assets;
using Blocwerk.Core.Assets.Manifest;
using Blocwerk.Core.Assets.Partition;
using Blocwerk.Core.Services;
using Blocwerk.Web.Endpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The release review's fixes on the hashed asset side: legacy renditions adopted rather than rendered again (W1a), a
/// confirmed mismatch that does not fail closed for good (W2), and a revoked kiosk key that stops every read (W4).
/// </summary>
public class AssetReleaseHardeningTests
{
    /// <summary>W1a: the first <c>/a/{sha}/{width}</c> after a deploy moves the legacy route's rendition over.</summary>
    [Fact]
    public async Task AHashedVariant_AdoptsTheRenditionTheLegacyPanelRouteMade()
    {
        using var f = new AssetRouteFixture();
        var s = await f.SeedAsync();
        var tag = await AssetUrlSitesTests.Panels(f).GetPanelPhotoTagAsync(f.Harness.WallId, s.PanelId);
        var legacy = ImageResponse.VariantKey(tag! with { Sha = null }, ImageIdentity.PanelPhoto(s.PanelId, ImageIdentity.LiveSlot));
        await f.Variants.GetOrCreateAsync(legacy, 640, () => Task.FromResult<byte[]?>(f.PanelBytes), default);
        var legacyFile = Directory.GetFiles(Path.Combine(f.VariantRoot, legacy.Identity)).Single();
        await File.WriteAllBytesAsync(legacyFile, [42, 42, 42]); // a marker no render produces

        var http = await f.GetAsync(f.Harness.WallId, s.Panel, width: 640);

        Assert.Equal(StatusCodes.Status200OK, http.Response.StatusCode);
        Assert.Equal([42, 42, 42], AssetRouteFixture.Body(http));
        Assert.False(File.Exists(legacyFile));
    }

    /// <summary>W1b: concurrent misses of one rendition share one load, and renders queue at the process-wide limit.</summary>
    [Fact]
    public async Task ConcurrentMisses_ShareOneLoad_AndRendersStayWithinTheLimit()
    {
        var flights = new RenditionFlights();
        var gate = new TaskCompletionSource();
        var loads = 0;
        var misses = Enumerable.Range(0, 5).Select(_ => flights.RunAsync("k", async () =>
        {
            Interlocked.Increment(ref loads);
            await gate.Task;
            return new ImageVariant([1], "image/jpeg", IsOriginal: false);
        }, CancellationToken.None)).ToList();
        gate.SetResult();
        await Task.WhenAll(misses);

        var running = 0;
        var peak = 0;
        var release = new TaskCompletionSource();
        var renders = Enumerable.Range(0, 6).Select(_ => Task.Run(() => flights.RenderAsync(() =>
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref peak, now);
            release.Task.Wait(TimeSpan.FromMilliseconds(100));
            Interlocked.Decrement(ref running);
            return 0;
        }, CancellationToken.None))).ToList();
        await Task.Delay(50);
        release.SetResult();
        await Task.WhenAll(renders);

        Assert.Equal(1, loads);
        Assert.Equal(RenditionFlights.RenderConcurrency, peak);
    }

    /// <summary>W2: bytes that no longer hash clear the row's hash, so it falls back to legacy and is hashed again.</summary>
    [Fact]
    public async Task AConfirmedMismatch_ClearsTheRowsHash_AndAnnouncesTheManifest()
    {
        using var f = new AssetRouteFixture();
        var s = await f.SeedAsync();
        await using (var db = f.Harness.CreateContext())
        {
            await db.WallPanels.Where(p => p.Id == s.PanelId).ExecuteUpdateAsync(u => u.SetProperty(p => p.Photo, new byte[] { 1, 2 }));
        }

        AssetRouteAccessTests.AssertRefused(await f.GetAsync(f.Harness.WallId, s.Panel));

        await using (var db = f.Harness.CreateContext())
        {
            Assert.Null(await db.WallPanels.Where(p => p.Id == s.PanelId).Select(p => p.PhotoSha256).SingleAsync());
            Assert.True(await db.WallChanges.AnyAsync(c => c.WallId == f.Harness.WallId && c.Kind == FeedKinds.WallManifest));
        }

        var panel = (await AssetUrlSitesTests.Panels(f).GetPanelsAsync(f.Harness.WallId)).Single(p => p.Id == s.PanelId);
        Assert.Equal($"/api/walls/{f.Harness.WallId}/panels/{s.PanelId}/photo", AssetUrls.Panel(f.Harness.WallId, panel));
    }

    /// <summary>W2 for files: a gallery file rewritten under its hash clears that hash too.</summary>
    [Fact]
    public async Task AMismatchedFile_ClearsItsHash()
    {
        using var f = new AssetRouteFixture();
        var s = await f.SeedAsync();
        string path;
        await using (var db = f.Harness.CreateContext())
        {
            path = f.ImageStorage.ResolvePhysicalPath(await db.WallImages.Select(i => i.StoragePath).SingleAsync())!;
        }

        await File.WriteAllBytesAsync(path, [6, 6, 6, 6]);

        AssetRouteAccessTests.AssertRefused(await f.GetAsync(f.Harness.WallId, s.Gallery));
        await using (var db = f.Harness.CreateContext())
        {
            Assert.Null(await db.WallImages.Select(i => i.Sha256).SingleAsync());
        }
    }

    /// <summary>W4: a revoked kiosk key gets no asset, anonymous or acting as a member.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ARevokedKioskKey_GetsNoAsset(bool actingAsAMember)
    {
        using var f = new AssetRouteFixture();
        var s = await f.SeedAsync();
        f.Caller = actingAsAMember ? f.Harness.Owner : null;
        f.BindKiosk(f.Harness.WallId);
        Assert.Equal(StatusCodes.Status200OK, (await f.GetAsync(f.Harness.WallId, s.Panel)).Response.StatusCode);

        Revoke(f);

        AssetRouteAccessTests.AssertRefused(await f.GetAsync(f.Harness.WallId, s.Panel));
        AssetRouteAccessTests.AssertRefused(await f.GetAsync(f.Harness.WallId, s.Panel, width: 640));
    }

    /// <summary>W4: and no manifest, no cache summary, and the <c>none</c> partition (which wipes the tablet's caches).</summary>
    [Fact]
    public async Task ARevokedKioskKey_GetsNoManifest_NoSummary_AndTheNonePartition()
    {
        using var f = new ManifestFixture();
        await f.SeedAsync();
        f.BindKiosk();
        var partitions = new CachePartitionResolver(new FixedPartitionKey(1), f.Assets.CurrentUser, f.Assets.Kiosk, f.Assets.KioskKeys);
        var summary = new CacheSummaryService(f.Assets.Harness.DbContextFactory, f.Assets.CurrentUser, f.Assets.Kiosk, f.Assets.KioskKeys);
        Assert.NotNull(await f.GetAsync());
        Assert.StartsWith(CachePartitions.KioskPrefix, await partitions.ResolveAsync());

        Revoke(f.Assets);

        Assert.Null(await f.GetAsync());
        Assert.Equal(CachePartitions.None, await partitions.ResolveAsync());
        Assert.Empty((await summary.GetAsync(default)).Walls);
    }

    private static void Revoke(AssetRouteFixture f) =>
        f.KioskKeys.IsKeyValidAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(false);

    private static void InterlockedMax(ref int target, int value)
    {
        var seen = Volatile.Read(ref target);
        while (value > seen && Interlocked.CompareExchange(ref target, value, seen) != seen)
        {
            seen = Volatile.Read(ref target);
        }
    }
}
