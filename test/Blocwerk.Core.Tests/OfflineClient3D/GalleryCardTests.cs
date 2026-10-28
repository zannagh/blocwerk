// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Client.Pages;
using static Blocwerk.Core.Tests.OfflineClient3D.View3DWorld;
using static Blocwerk.Core.Tests.OfflineClientStore.StoreWorld;

namespace Blocwerk.Core.Tests.OfflineClient3D;

/// <summary>
/// The gallery below the wall's boulder list (the Server's WallGalleryPanel, read-only): the cached gallery document's
/// snapshots from the rendition the device holds (640, then the original), 24 at a time; no card without the document.
/// </summary>
public sealed class GalleryCardTests
{
    [Fact]
    public async Task TheCachedGallery_ShowsItsSnapshots_FromThe640Rendition()
    {
        await using var view = await StartAsync(Device3D(UserA, [Image(1, "Monday"), Image(2)]), $"walls/{Wall}", new FakeClient3D());

        var html = await view.RenderAsync<WallViewPage>(new() { ["WallId"] = Wall });

        Assert.Contains("wall-gallery-panel", html, StringComparison.Ordinal);
        Assert.Contains("2 images", html, StringComparison.Ordinal);
        Assert.Equal(2, html.Split("class=\"wall-gallery-thumb\"").Length - 1);
        Assert.Contains($"src=\"/api/walls/{Wall}/a/{Image(1).Sha}/640\"", html, StringComparison.Ordinal);
        Assert.Contains("alt=\"Monday\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Delete", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AShareLink_CarriesItsToken()
    {
        await using var view = await StartAsync(Device3D(Anon, [Image(1)]), $"walls/{Wall}/shared/tok", new FakeClient3D());

        var html = await view.RenderAsync<WallViewPage>(new() { ["WallId"] = Wall, ["ShareToken"] = "tok" });

        Assert.Contains($"/a/{Image(1).Sha}/640?token=tok", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALongGallery_Shows24_AndLoadMore()
    {
        await using var view = await StartAsync(Device3D(UserA, Enumerable.Range(1, 30).Select(i => Image(i)).ToList()), $"walls/{Wall}", new FakeClient3D());

        var html = await view.RenderAsync<WallViewPage>(new() { ["WallId"] = Wall });

        Assert.Equal(24, html.Split("class=\"wall-gallery-thumb\"").Length - 1);
        Assert.Contains("Load more", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoCachedGalleryDocument_NoCard()
    {
        await using var view = await StartAsync(Device3D(UserA), $"walls/{Wall}", new FakeClient3D());

        var html = await view.RenderAsync<WallViewPage>(new() { ["WallId"] = Wall });

        Assert.DoesNotContain("wall-gallery-panel", html, StringComparison.Ordinal);
    }
}
