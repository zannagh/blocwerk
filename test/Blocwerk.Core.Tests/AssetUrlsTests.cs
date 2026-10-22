// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Assets;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Services;
using Blocwerk.Web.Endpoints;

namespace Blocwerk.Core.Tests;

/// <summary>
/// <see cref="AssetUrls"/>, the one builder of asset URLs: a row with a hash gets <c>/api/walls/{w}/a/{sha}</c>, a row
/// without one the legacy route with the version it always carried. Widths are a path segment on the hashed form.
/// </summary>
public class AssetUrlsTests
{
    private static readonly Guid Wall = Guid.Parse("0b0e8a8e-3c2a-4d7e-9a51-6f1f1f1f1f1f");
    private static readonly Guid PanelId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly string Sha = AssetRouteFixture.Hex([1, 2, 3]);

    [Fact]
    public void AHashedPanel_GetsTheContentAddressedUrl_WithTheTokenAndWithoutTheRevision()
    {
        var panel = new WallPanelInfo(PanelId, 0, 0, true, false, 1, false, PhotoRevision: 2, PhotoSha: Sha);

        Assert.Equal($"/api/walls/{Wall}/a/{Sha}", AssetUrls.Panel(Wall, panel));
        Assert.Equal($"/api/walls/{Wall}/a/{Sha}?token=t%2Fx", AssetUrls.Panel(Wall, panel, "t/x"));
    }

    [Fact]
    public void AnUnhashedPanel_KeepsTheLegacyRoute_WithTokenAndRevision()
    {
        var panel = new WallPanelInfo(PanelId, 0, 0, true, false, 1, false, PhotoRevision: 2);

        Assert.Equal($"/api/walls/{Wall}/panels/{PanelId}/photo?rev=2", AssetUrls.Panel(Wall, panel));
        Assert.Equal($"/api/walls/{Wall}/panels/{PanelId}/photo?token=t&rev=2", AssetUrls.Panel(Wall, panel, "t"));
        Assert.Equal($"/api/walls/{Wall}/panels/{PanelId}/photo", AssetUrls.Panel(Wall, PanelId, null));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ABCDEF")]
    [InlineData("zz")]
    public void AMissingOrMalformedSha_CountsAsNone(string? sha)
    {
        Assert.Null(AssetUrls.Hashed(Wall, sha));
        Assert.Null(AssetUrls.Hashed(Wall, sha?.ToUpperInvariant()));
        Assert.Equal($"/api/walls/{Wall}/photo", AssetUrls.WallPhoto(Wall, sha));
        Assert.Null(AssetUrls.Hashed(Wall, Sha.ToUpperInvariant()));
    }

    [Fact]
    public void TheWallPhoto_IsHashed_OrTheLiveOrGenerationRoute()
    {
        Assert.Equal($"/api/walls/{Wall}/a/{Sha}", AssetUrls.WallPhoto(Wall, Sha, generation: 3));
        Assert.Equal($"/api/walls/{Wall}/photo/3?token=t", AssetUrls.WallPhoto(Wall, null, "t", 3));
        Assert.Equal($"/api/walls/{Wall}/photo", AssetUrls.WallPhoto(Wall, null));
    }

    [Fact]
    public void AVideoThumbnail_IsHashedOnItsWall_OrTheLegacyThumbnailRoute()
    {
        Assert.Equal($"/api/walls/{Wall}/a/{Sha}?token=t", AssetUrls.VideoThumbnail(Wall, PanelId, Sha, "t"));
        Assert.Equal($"/api/beta-videos/{PanelId}/thumbnail?token=t", AssetUrls.VideoThumbnail(Wall, PanelId, null, "t"));
    }

    [Fact]
    public void AWidth_IsAPathSegmentOnAHashedUrl_AndAQueryOnALegacyOne()
    {
        var hashed = AssetUrls.Panel(Wall, PanelId, Sha, "t");

        Assert.Equal($"/api/walls/{Wall}/a/{Sha}/1280?token=t", ResponsiveImage.Initial(hashed));
        Assert.Equal($"/api/walls/{Wall}/a/{Sha}/640?token=t", ResponsiveImage.AtWidth(ResponsiveImage.Initial(hashed), 640));
        Assert.Equal($"/api/walls/{Wall}/a/{Sha}?token=t", ResponsiveImage.AtWidth(hashed, 1000));
        Assert.Equal($"/api/walls/{Wall}/panels/{PanelId}/photo?w=640", ResponsiveImage.AtWidth(AssetUrls.Panel(Wall, PanelId, null), 640));
        Assert.True(AssetUrls.IsHashed($"/api/walls/{Wall}/a/{Sha}/640?token=t"));
        Assert.False(AssetUrls.IsHashed($"/api/walls/{Wall}/panels/{PanelId}/photo"));
    }

    [Fact]
    public void AGalleryItem_IsHashed_WhenItHasAHash_ElseTheVersionedMediaRoute()
    {
        var legacy = ResponsiveImage.Gallery(Wall, "Uploaded", PanelId, 42, "t", thumbnail: true);
        var hashed = ResponsiveImage.Gallery(Wall, "Uploaded", PanelId, 42, "t", thumbnail: true, Sha);

        Assert.StartsWith($"/media/walls/{Wall}/gallery/uploaded/{PanelId}?token=t&v=", legacy);
        Assert.EndsWith("&w=640", legacy);
        Assert.Equal($"/api/walls/{Wall}/a/{Sha}/640?token=t", hashed);
        Assert.Equal($"/api/walls/{Wall}/a/{Sha}", ResponsiveImage.Gallery(Wall, "ResetPhoto", PanelId, 42, null, thumbnail: false, Sha));
    }

    [Fact]
    public void ASplatLevel_IsHashedByItsOwnFile_AndAnUnhashedOneKeepsTheVersionedRoute()
    {
        var lodSha = AssetRouteFixture.Hex([9, 9]);
        var splat = new WallGeometrySplat
        {
            GeometryModelId = Guid.NewGuid(), StoredPath = "a.spz", FrameJson = "{}", SizeBytes = 10, Sha256 = ContentHash.Of([1, 2, 3]),
            MobileStoredPath = "m.spz", MobileSizeBytes = 5,
            LodLevelsJson = SplatLodLadder.Serialize([new SplatLodLevel(40_000, "l.spz", 2, lodSha)]),
        };

        Assert.Equal($"/api/walls/{Wall}/a/{Sha}?token=t", WallGeometrySplats.Url(Wall, splat, "t"));
        Assert.Equal($"/api/walls/{Wall}/a/{lodSha}", WallGeometrySplats.LevelUrl(Wall, splat, 40_000));
        Assert.Contains("/splat?lod=mobile&v=", WallGeometrySplats.Url(Wall, splat, mobile: true));

        // A level the row lacks serves the full scene, so it is the full scene's hash.
        Assert.Equal($"/api/walls/{Wall}/a/{Sha}", WallGeometrySplats.LevelUrl(Wall, splat, 77));

        splat.Sha256 = null;
        Assert.Contains($"/geometry/{splat.GeometryModelId}/splat?token=t&v=", WallGeometrySplats.Url(Wall, splat, "t"));
    }
}
