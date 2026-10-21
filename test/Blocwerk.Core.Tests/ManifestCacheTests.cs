// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Assets;
using Blocwerk.Core.Assets.Manifest;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The server's manifest cache (design §6.4, S7): reused while the wall's feed holds nothing that can change a manifest
/// input, rebuilt on a <c>wall.manifest</c> or any other related row, per audience class, and aged out.
/// </summary>
public class ManifestCacheTests
{
    [Fact]
    public async Task UnrelatedRows_KeepTheCachedManifest_ARelatedRowRebuildsIt()
    {
        using var f = new ManifestFixture();
        await f.SeedAsync();
        var first = (await f.GetAsync())!;
        var added = await AddUnannouncedImageAsync(f);

        await f.AddFeedRowAsync(FeedKinds.CommentAdded);
        await f.AddFeedRowAsync(FeedKinds.HoldChanged);
        var cached = (await f.GetAsync())!;
        Assert.Equal(first.Sha, cached.Sha);
        Assert.NotEqual(first.Cursor, cached.Cursor);

        await f.AddFeedRowAsync(FeedKinds.WallManifest);
        var rebuilt = (await f.GetAsync())!;
        Assert.NotEqual(first.Sha, rebuilt.Sha);
        Assert.Contains(rebuilt.Assets, a => a.Sha == added);
    }

    [Theory]
    [InlineData(FeedKinds.PanelChanged)]
    [InlineData(FeedKinds.WallUpdated)]
    [InlineData(FeedKinds.GalleryChanged)]
    [InlineData(FeedKinds.View3DChanged)]
    [InlineData(FeedKinds.BoulderChanged)]
    [InlineData(FeedKinds.VideoAdded)]
    [InlineData(FeedKinds.WallResync)]
    [InlineData("some.future.kind")]
    public async Task ARowThatCanChangeAnInput_RebuildsTheManifest(string kind)
    {
        using var f = new ManifestFixture();
        await f.SeedAsync();
        var first = (await f.GetAsync())!;
        await AddUnannouncedImageAsync(f);

        await f.AddFeedRowAsync(kind);

        Assert.NotEqual(first.Sha, (await f.GetAsync())!.Sha);
    }

    [Fact]
    public async Task TheCache_IsPerAudienceClass()
    {
        using var f = new ManifestFixture();
        await f.SeedAsync();
        var member = (await f.GetAsync())!;

        f.Assets.Caller = null;
        var share = (await f.GetAsync(AssetRouteFixture.ShareToken))!;

        Assert.NotEqual(member.Sha, share.Sha);
        Assert.Equal(2, f.Cache.Count);
        f.Assets.Caller = f.Assets.Harness.Owner;
        Assert.Equal(member.Sha, (await f.GetAsync())!.Sha);
    }

    [Fact]
    public async Task AnEntry_AgesOut_SoonerWhileItListsUnhashedRows()
    {
        using var f = new ManifestFixture();
        await f.SeedAsync();
        var first = (await f.GetAsync())!;
        await AddUnannouncedImageAsync(f);

        f.Time.Advance(WallManifestService.MaxAge - TimeSpan.FromSeconds(1));
        Assert.Equal(first.Sha, (await f.GetAsync())!.Sha);
        f.Time.Advance(TimeSpan.FromSeconds(1));
        var aged = (await f.GetAsync())!;
        Assert.NotEqual(first.Sha, aged.Sha);

        await AddUnannouncedImageAsync(f, hashed: false);
        Assert.Equal(aged.Sha, (await f.GetAsync())!.Sha);
        f.Time.Advance(WallManifestService.MaxAge);
        var pending = (await f.GetAsync())!;
        Assert.Equal(1, pending.Pending);

        await AddUnannouncedImageAsync(f);
        f.Time.Advance(WallManifestService.PendingMaxAge);
        Assert.NotEqual(pending.Sha, (await f.GetAsync())!.Sha);
    }

    /// <summary>A gallery row written without the feed interceptor: an input change that no row announces.</summary>
    private static async Task<string> AddUnannouncedImageAsync(ManifestFixture f, bool hashed = true)
    {
        byte[] bytes = [.. Guid.NewGuid().ToByteArray()];
        var stored = $"{Guid.NewGuid():N}.jpg";
        await File.WriteAllBytesAsync(f.Assets.ImageStorage.ResolvePhysicalPath(stored)!, bytes);
        await using var db = f.Assets.Harness.CreateContext();
        db.WallImages.Add(new WallImage
        {
            WallId = f.WallId, StoragePath = stored, ContentType = "image/jpeg", SizeBytes = bytes.Length,
            Sha256 = hashed ? ContentHash.Of(bytes) : null,
        });
        await db.SaveChangesAsync();
        Assert.Empty(db.ChangeTracker.Entries<WallChange>().Where(e => e.Entity.Audience == FeedAudience.Wall));
        return AssetRouteFixture.Hex(bytes);
    }
}
