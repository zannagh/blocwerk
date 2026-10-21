// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Security.Claims;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Configuration;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Blocwerk.Web.Endpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The privacy pass rewrites an uploaded gallery file (and an archived reset photo) in place under the
/// same route, so the gallery hands out urls that name the bytes (<c>?v=</c>), and every url, versioned or not,
/// revalidates against the ETag (<c>private, no-cache</c>): the browser's HTTP cache is shared by every identity on
/// the device, so no copy is reused without asking the gate.
/// </summary>
public sealed class StaleGalleryImageCacheTests : IDisposable
{
    private readonly List<string> cacheRoots = [];

    public void Dispose()
    {
        foreach (var root in cacheRoots.Where(Directory.Exists))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(WallGallerySource.Uploaded)]
    [InlineData(WallGallerySource.ResetPhoto)]
    public async Task ThePrivacyRewrite_GivesTheImageANewUrl_AndEveryUrlRevalidates(WallGallerySource source)
    {
        using var h = new WallTestHarness();
        await SeedDirtyGalleryAsync(h);
        var before = await UrlAsync(h, source, thumbnail: false);
        var beforeThumb = await UrlAsync(h, source, thumbnail: true);

        var (freshResult, fresh) = await ServeAsync(h, before);
        Assert.Equal(ImageResponse.MutableCacheControl, fresh.Response.Headers.CacheControl.ToString());

        await new WallPhotoPrivacyService(
            h.DbContextFactory, h.CurrentUser, h.WallImageStorage, NullLogger<WallPhotoPrivacyService>.Instance)
            .RemoveMetadataAsync(h.WallId);

        var after = await UrlAsync(h, source, thumbnail: false);
        Assert.NotEqual(before, after);
        Assert.NotEqual(beforeThumb, await UrlAsync(h, source, thumbnail: true));

        // The url the page held before the rewrite now names bytes that are gone: revalidate.
        var (_, stale) = await ServeAsync(h, before);
        Assert.Equal(ImageResponse.MutableCacheControl, stale.Response.Headers.CacheControl.ToString());
        var (_, staleThumb) = await ServeAsync(h, beforeThumb);
        Assert.Equal(ImageResponse.MutableCacheControl, staleThumb.Response.Headers.CacheControl.ToString());

        var (currentResult, current) = await ServeAsync(h, after);
        Assert.Equal(ImageResponse.MutableCacheControl, current.Response.Headers.CacheControl.ToString());
        Assert.NotEqual(EtagOf(freshResult, fresh), EtagOf(currentResult, current));
    }

    [Theory]
    [InlineData(WallGallerySource.Uploaded)]
    [InlineData(WallGallerySource.ResetPhoto)]
    public async Task AnUnversionedUrl_FromAnOldClient_IsServedWithRevalidation(WallGallerySource source)
    {
        using var h = new WallTestHarness();
        await SeedDirtyGalleryAsync(h);
        var url = (await UrlAsync(h, source, thumbnail: false)).Split('?')[0];

        var (result, http) = await ServeAsync(h, url);

        Assert.Equal(ImageResponse.MutableCacheControl, http.Response.Headers.CacheControl.ToString());
        Assert.False(string.IsNullOrEmpty(EtagOf(result, http)));

        // An old client's thumbnail never named a version: it revalidates as well.
        var (_, thumb) = await ServeAsync(h, ResponsiveImage.AtWidth(url, ResponsiveImage.ThumbnailWidth));
        Assert.Equal(ImageResponse.MutableCacheControl, thumb.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public void TheGalleryUrl_CarriesTheVersionOfItsSize()
    {
        var wall = Guid.NewGuid();
        var id = Guid.NewGuid();

        var url = ResponsiveImage.Gallery(wall, "Uploaded", id, 1000, null, thumbnail: false);

        Assert.Equal(ResponsiveImage.GalleryVersion("uploaded", id, 1000), AssetRequests.Query(url, AssetVersion.QueryName));
        Assert.NotEqual(url, ResponsiveImage.Gallery(wall, "Uploaded", id, 999, null, thumbnail: false));
    }

    /// <summary>The streamed upload hands its ETag to the file result; the blob-backed sources set the header.</summary>
    private static string EtagOf(IResult result, HttpContext http) =>
        result is PhysicalFileHttpResult file ? file.EntityTag!.ToString() : http.Response.Headers.ETag.ToString();

    private static async Task<string> UrlAsync(WallTestHarness h, WallGallerySource source, bool thumbnail)
    {
        var item = (await h.WallImageService.GetGalleryAsync(h.WallId)).Single(i => i.Source == source);
        return ResponsiveImage.Gallery(h.WallId, item.Source.ToString(), item.Id, item.SizeBytes, null, thumbnail);
    }

    private async Task<(IResult Result, HttpContext Http)> ServeAsync(WallTestHarness h, string url)
    {
        var http = AssetRequests.For(url);
        return (await ServeAsync(h, url, http), http);
    }

    private Task<IResult> ServeAsync(WallTestHarness h, string url, HttpContext http)
    {
        var parts = url.Split('?')[0].Split('/');
        var width = AssetRequests.Query(url, "w");
        var kiosk = Substitute.For<IKioskContext>();
        return WallGalleryImageEndpoint.HandleAsync(
            h.WallId, parts[^2], Guid.Parse(parts[^1]), null, width is null ? null : int.Parse(width),
            new ClaimsPrincipal(), http, h.WallService, h.WallImageService, h.WallImageStorage, h.CurrentUser,
            h.DbContextFactory, kiosk, NewCache(), CancellationToken.None);
    }

    private static async Task SeedDirtyGalleryAsync(WallTestHarness h)
    {
        await h.SeedWallAsync(holdCount: 0, generation: 2);
        var dirty = PrivacyPhotos.GpsJpeg();
        await using var db = h.CreateContext();
        db.WallResets.Add(new WallReset
        {
            WallId = h.WallId, Generation = 1, PreviousPhoto = dirty, PreviousPhotoContentType = "image/jpeg", ResetByUserId = h.Owner.Id,
        });
        var temp = h.WallImageStorage.CreateTempPath(".jpg");
        await File.WriteAllBytesAsync(temp, dirty);
        var stored = h.WallImageStorage.Commit(temp, ".jpg");
        db.WallImages.Add(new WallImage { WallId = h.WallId, StoragePath = stored, ContentType = "image/jpeg", SizeBytes = dirty.Length });
        await db.SaveChangesAsync();
    }

    private FileSystemImageVariantCache NewCache()
    {
        var settings = new BlocwerkSettings();
        settings.WallImage.StoragePath = Path.Combine(Path.GetTempPath(), "bwk-stale-gallery", Guid.NewGuid().ToString("N"));
        cacheRoots.Add(settings.WallImage.StoragePath);
        return new FileSystemImageVariantCache(settings, NullLogger<FileSystemImageVariantCache>.Instance);
    }
}
