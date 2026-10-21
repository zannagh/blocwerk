// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Assets;
using Blocwerk.Core.Assets.Resolution;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// <see cref="AssetResolver"/>: a sha resolves through the hash columns, only under its own wall, only while the row
/// still holds it, and its bytes are only handed out when they still hash to it. A stale hash serving the wrong bytes
/// under an immutable URL is the worst failure this route can have, so most of these pin some form of "not found".
/// </summary>
public class AssetResolverTests
{
    [Fact]
    public Task EveryKind_ResolvesToItsRow() => EveryKindAsync(new AssetRouteFixture());

    [PostgresFact]
    [Trait("Db", "Postgres")]
    public Task EveryKind_ResolvesToItsRow_OnPostgres() => EveryKindAsync(new AssetRouteFixture(PostgresTestDatabase.Create()));

    [Fact]
    public Task AShaOfAnotherWall_IsNotFound_EvenForAMemberOfBoth() => CrossWallAsync(new AssetRouteFixture());

    [PostgresFact]
    [Trait("Db", "Postgres")]
    public Task AShaOfAnotherWall_IsNotFound_EvenForAMemberOfBoth_OnPostgres() =>
        CrossWallAsync(new AssetRouteFixture(PostgresTestDatabase.Create()));

    [Fact]
    public Task ARedactedPhoto_IsFoundOnlyUnderItsNewHash() => RedactedAsync(new AssetRouteFixture());

    [PostgresFact]
    [Trait("Db", "Postgres")]
    public Task ARedactedPhoto_IsFoundOnlyUnderItsNewHash_OnPostgres() => RedactedAsync(new AssetRouteFixture(PostgresTestDatabase.Create()));

    [Fact]
    public async Task AnUnhashedRow_IsNotFound()
    {
        using var f = new AssetRouteFixture();
        var seeded = await f.SeedAsync();
        await using (var db = f.Harness.CreateContext())
        {
            await db.WallPanels.Where(p => p.Id == seeded.PanelId).ExecuteUpdateAsync(s => s.SetProperty(p => p.PhotoSha256, (byte[]?)null));
        }

        Assert.Null(await f.Resolver().ResolveAsync(f.Harness.WallId, seeded.Panel, null, default));
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("zz")]
    public async Task AMalformedSha_IsNotFound(string sha)
    {
        using var f = new AssetRouteFixture();
        await f.SeedAsync();

        Assert.Null(await f.Resolver().ResolveAsync(f.Harness.WallId, sha, null, default));
    }

    /// <summary>One asset, one URL: the uppercase spelling of a real sha is not a second address for it.</summary>
    [Fact]
    public async Task AnUppercaseSha_IsNotFound()
    {
        using var f = new AssetRouteFixture();
        var seeded = await f.SeedAsync();

        Assert.Null(await f.Resolver().ResolveAsync(f.Harness.WallId, seeded.Panel.ToUpperInvariant(), null, default));
    }

    /// <summary>A writer that changed the bytes without the hash (raw SQL): the row still resolves, its bytes do not load.</summary>
    [Fact]
    public async Task BytesThatNoLongerHashToTheSha_AreNotLoaded()
    {
        using var f = new AssetRouteFixture();
        var seeded = await f.SeedAsync();
        var resolver = f.Resolver();
        var asset = await resolver.ResolveAsync(f.Harness.WallId, seeded.Panel, null, default);
        await using (var db = f.Harness.CreateContext())
        {
            await db.WallPanels.Where(p => p.Id == seeded.PanelId).ExecuteUpdateAsync(s => s.SetProperty(p => p.Photo, new byte[] { 9, 9 }));
        }

        Assert.NotNull(asset);
        Assert.Null(await resolver.LoadAsync(asset, default));
    }

    [Fact]
    public async Task AFileRewrittenOnDisk_IsNotLoaded_AndASplatRewrittenOnDisk_FailsVerification()
    {
        using var f = new AssetRouteFixture();
        var seeded = await f.SeedAsync();
        var resolver = f.Resolver();
        var gallery = await resolver.ResolveAsync(f.Harness.WallId, seeded.Gallery, null, default);
        var splat = await resolver.ResolveAsync(f.Harness.WallId, seeded.Splat, null, default);
        Assert.True(await resolver.VerifyFileAsync(splat!, default));

        await File.WriteAllBytesAsync(gallery!.PhysicalPath!, [42, 42, 42]);
        await File.WriteAllBytesAsync(splat!.PhysicalPath!, [43, 43, 43, 43, 43]);

        Assert.Null(await resolver.LoadAsync(gallery, default));
        Assert.False(await resolver.VerifyFileAsync(splat, default));
    }

    private static async Task EveryKindAsync(AssetRouteFixture f)
    {
        using (f)
        {
            var s = await f.SeedAsync();
            var resolver = f.Resolver();
            var expected = new Dictionary<string, AssetKind>
            {
                [s.Panel] = AssetKind.Panel, [s.WallPhoto] = AssetKind.WallPhoto, [s.ResetPhoto] = AssetKind.ResetPhoto,
                [s.Gallery] = AssetKind.Gallery, [s.Texture] = AssetKind.Texture, [s.Mask] = AssetKind.Mask,
                [s.Splat] = AssetKind.Splat, [s.SplatLod] = AssetKind.Splat, [s.VideoThumbnail] = AssetKind.VideoThumbnail,
                [s.DraftVideoThumbnail] = AssetKind.VideoThumbnail,
            };

            foreach (var (sha, kind) in expected)
            {
                var asset = await resolver.ResolveAsync(f.Harness.WallId, sha, null, default);
                Assert.True(asset?.Kind == kind, $"{kind} {sha} resolved to {asset?.Kind.ToString() ?? "nothing"}");
                Assert.Equal(sha, asset!.Hex);
                if (!asset.IsStreamed)
                {
                    Assert.Equal(sha, ContentHash.ToHex(ContentHash.Of(await resolver.LoadAsync(asset, default))));
                }
            }
        }
    }

    private static async Task CrossWallAsync(AssetRouteFixture f)
    {
        using (f)
        {
            var s = await f.SeedAsync();
            await using (var db = f.Harness.CreateContext())
            {
                db.WallMembers.Add(new WallMember { WallId = s.OtherWallId, UserId = f.Harness.Owner.Id, Role = WallRole.Member });
                await db.SaveChangesAsync();
            }

            var resolver = f.Resolver();
            Assert.NotNull(await resolver.ResolveAsync(s.OtherWallId, s.OtherWallPanel, null, default));
            Assert.Null(await resolver.ResolveAsync(f.Harness.WallId, s.OtherWallPanel, null, default));
            foreach (var sha in new[] { s.Panel, s.Gallery, s.Texture, s.Mask, s.Splat, s.SplatLod, s.VideoThumbnail })
            {
                Assert.Null(await resolver.ResolveAsync(s.OtherWallId, sha, null, default));
            }
        }
    }

    /// <summary>The privacy rewrite replaces bytes and hash in one statement; the location-tagged copy's sha is gone.</summary>
    private static async Task RedactedAsync(AssetRouteFixture f)
    {
        using (f)
        {
            var s = await f.SeedAsync();
            byte[] clean = [1, 2];
            await using (var db = f.Harness.CreateContext())
            {
                await db.WallPanels.Where(p => p.Id == s.PanelId)
                    .ExecuteUpdateAsync(u => u.SetProperty(p => p.Photo, clean).SetProperty(p => p.PhotoSha256, ContentHash.Of(clean)));
            }

            var resolver = f.Resolver();
            Assert.Null(await resolver.ResolveAsync(f.Harness.WallId, s.Panel, null, default));
            var redacted = await resolver.ResolveAsync(f.Harness.WallId, AssetRouteFixture.Hex(clean), null, default);
            Assert.Equal(clean, await resolver.LoadAsync(redacted!, default));
        }
    }
}
