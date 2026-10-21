// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Assets;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The manifest per audience: it lists exactly what that caller may fetch through <c>/a/</c>, and every sha it lists is
/// fetched as that caller to prove it (a manifest never reveals a sha its viewer cannot fetch).
/// </summary>
public class ManifestAudienceTests
{
    [Fact]
    public async Task AMember_GetsEveryKind_DraftThumbnailsIncluded()
    {
        using var f = new ManifestFixture();
        var s = await f.SeedAsync();
        f.Assets.Caller = await f.Assets.Harness.AddMemberAsync("member@test", WallRole.Member);

        var manifest = await f.GetAsync();

        Assert.Equal(Everything(s).Order(), Shas(manifest).Order());
        await AssertAllFetchableAsync(f, manifest!);
    }

    [Theory]
    [InlineData(WallRole.Admin)]
    [InlineData(WallRole.Moderator)]
    public async Task ASetter_GetsTheMembersManifest(WallRole role)
    {
        using var f = new ManifestFixture();
        await f.SeedAsync();
        f.Assets.Caller = await f.Assets.Harness.AddMemberAsync("member@test", WallRole.Member);
        var member = await f.GetAsync();

        f.Assets.Caller = await f.Assets.Harness.AddMemberAsync($"{role}@test", role);
        var setter = await f.GetAsync();

        Assert.Equal(member!.Sha, setter!.Sha);
        f.Assets.Caller = f.Assets.Harness.Owner;
        Assert.Equal(member.Sha, (await f.GetAsync())!.Sha);
    }

    [Fact]
    public async Task AShareLinkViewer_GetsNoDraftThumbnail_AnonymousOrSignedIn()
    {
        using var f = new ManifestFixture();
        var s = await f.SeedAsync();
        var expected = Everything(s).Where(sha => sha != s.DraftVideoThumbnail).Order().ToList();

        f.Assets.Caller = null;
        var anonymous = await f.GetAsync(AssetRouteFixture.ShareToken);
        Assert.Equal(expected, Shas(anonymous).Order());
        await AssertAllFetchableAsync(f, anonymous!, AssetRouteFixture.ShareToken);

        f.Assets.Caller = new User { Identifier = "nobody@test" };
        Assert.Equal(anonymous!.Sha, (await f.GetAsync(AssetRouteFixture.ShareToken))!.Sha);
    }

    [Fact]
    public async Task AnAnonymousKiosk_GetsNoVideoThumbnails()
    {
        using var f = new ManifestFixture();
        var s = await f.SeedAsync();
        f.BindKiosk();

        var manifest = await f.GetAsync();

        Assert.Equal(Everything(s).Where(sha => sha != s.VideoThumbnail && sha != s.DraftVideoThumbnail).Order(), Shas(manifest).Order());
        Assert.DoesNotContain(manifest!.Assets, a => a.Kind == ManifestAssetKinds.VideoThumbnail);
        await AssertAllFetchableAsync(f, manifest);
    }

    /// <summary>The tablet's partition is shared by every identity it takes, so act-as gets the anonymous tablet's list.</summary>
    [Fact]
    public async Task AKioskActingAsAMember_GetsTheAnonymousKiosksManifest()
    {
        using var f = new ManifestFixture();
        await f.SeedAsync();
        f.BindKiosk();
        var anonymous = await f.GetAsync();

        f.Assets.Caller = await f.Assets.Harness.AddMemberAsync("member@test", WallRole.Member);
        var actingAs = await f.GetAsync();

        Assert.Equal(anonymous!.Sha, actingAs!.Sha);
        Assert.DoesNotContain(actingAs.Assets, a => a.Kind == ManifestAssetKinds.VideoThumbnail);
    }

    /// <summary>
    /// A tablet that may not cache its wall (today: its wall is unknown, or it carries no key) gets no manifest, even
    /// acting as a member, and its partition is <c>none</c> (<see cref="CachePartitionTests"/>).
    /// </summary>
    [Fact]
    public async Task AKioskThatMayNotCacheTheWall_GetsNoManifest()
    {
        using var f = new ManifestFixture();
        var s = await f.SeedAsync();

        f.BindKiosk(Guid.Empty);
        Assert.Null(await f.GetAsync());
        f.Assets.Caller = await f.Assets.Harness.AddMemberAsync("member@test", WallRole.Member);
        Assert.Null(await f.GetAsync());

        f.BindKiosk();
        f.Assets.Kiosk.KioskApiKeyId.Returns((Guid?)null);
        Assert.Null(await f.GetAsync());

        f.BindKiosk(s.OtherWallId);
        Assert.Null(await f.GetAsync());
        Assert.Null(await f.GetAsync(AssetRouteFixture.ShareToken));
    }

    [Fact]
    public async Task ANonMember_AnAnonymousCaller_AndARotatedToken_GetNoManifest()
    {
        using var f = new ManifestFixture();
        await f.SeedAsync();

        f.Assets.Caller = new User { Identifier = "nobody@test" };
        Assert.Null(await f.GetAsync());
        f.Assets.Caller = null;
        Assert.Null(await f.GetAsync());
        await using (var db = f.Assets.Harness.CreateContext())
        {
            await db.Walls.Where(w => w.Id == f.WallId).ExecuteUpdateAsync(u => u.SetProperty(w => w.ShareToken, "rotated"));
        }

        Assert.Null(await f.GetAsync(AssetRouteFixture.ShareToken));
        Assert.Equal(StatusCodes.Status404NotFound, (await f.GetHttpAsync(AssetRouteFixture.ShareToken)).Response.StatusCode);
    }

    internal static IEnumerable<string> Everything(SeededAssets s) =>
    [
        s.Panel, s.WallPhoto, s.ResetPhoto, s.Gallery, s.Texture, s.Mask, s.Splat, s.SplatLod, s.VideoThumbnail, s.DraftVideoThumbnail,
    ];

    internal static IEnumerable<string> Shas(WallManifest? manifest)
    {
        Assert.NotNull(manifest);
        Assert.All(manifest.Assets, a => Assert.NotNull(a.Sha));
        return manifest.Assets.Select(a => a.Sha!);
    }

    /// <summary>
    /// Every listed sha is a 200 for the same caller, and so is every listed width of the panel (the fixture's one real
    /// JPEG; its other photos are a few bytes that no renderer decodes).
    /// </summary>
    private static async Task AssertAllFetchableAsync(ManifestFixture f, WallManifest manifest, string? token = null)
    {
        foreach (var asset in manifest.Assets)
        {
            var original = await f.Assets.GetAsync(f.WallId, asset.Sha!, token: token, method: HttpMethods.Head);
            Assert.True(original.Response.StatusCode == StatusCodes.Status200OK, $"{asset.Kind} {asset.Sha} is listed but not fetchable");
            foreach (var width in asset.Kind == ManifestAssetKinds.Panel ? asset.Widths! : [])
            {
                var variant = await f.Assets.GetAsync(f.WallId, asset.Sha!, width, token, HttpMethods.Head);
                Assert.True(variant.Response.StatusCode == StatusCodes.Status200OK, $"{asset.Kind} {asset.Sha}/{width} is listed but not fetchable");
            }
        }
    }
}
