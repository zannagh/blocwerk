// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Blocwerk.Web.State;
using Blocwerk.Web.State.Live;
using Microsoft.AspNetCore.Components;

namespace Blocwerk.Web.Components.Pages.Walls;

/// <summary>
/// The live side of the gallery (design §5.5). A <c>gallery.deleted</c> drops the snapshot by id; a <c>gallery.changed</c>
/// re-reads the loaded range: the envelope carries no item, and an upload's capture time decides where it sorts in.
/// While the lightbox is open the gallery is held, so the image on screen and its neighbours never shift under the
/// viewer; closing it applies what arrived meanwhile.
/// </summary>
public partial class WallGalleryPanel
{
    private static readonly IReadOnlySet<string> Kinds = new HashSet<string> { FeedKinds.GalleryChanged, FeedKinds.GalleryDeleted };
    private static readonly IReadOnlySet<string> Removals = new HashSet<string> { FeedKinds.GalleryDeleted };
    private static readonly IReadOnlySet<string> NoUpserts = new HashSet<string>();

    private LeafFeed? feed;
    private (Guid Wall, string? Token)? feedKey;

    [Inject]
    private WallCacheState Cache { get; set; } = null!;

    [Inject]
    private ILogger<WallGalleryPanel> Logger { get; set; } = null!;

    public void Dispose() => feed?.Dispose();

    protected override async Task OnParametersSetAsync()
    {
        if (feedKey == (WallId, ShareToken))
        {
            return;
        }

        feedKey = (WallId, ShareToken);
        feed?.Dispose();

        // Share routes are not live, as on the wall and boulder pages.
        feed = string.IsNullOrEmpty(ShareToken)
            ? LeafFeed.Open(Cache, new LeafScope(WallId, null, Kinds), InvokeAsync, ApplyFeedAsync, () => Selected is not null || busy, Logger)
            : null;
        items.Clear();
        selectedIndex = -1;
        await LoadMore();
    }

    // On the dispatcher, never while the lightbox is open.
    private async Task ApplyFeedAsync(LeafBatch batch)
    {
        var plan = LeafPlan.Of(batch, NoUpserts, Removals);
        if (plan.Reload)
        {
            await ReadRangeAsync(Math.Max(items.Count, PageSize));
        }
        else
        {
            items.RemoveAll(i => i.Source == WallGallerySource.Uploaded && plan.Removals.Contains(i.Id));
        }

        StateHasChanged();
    }

    // The newest take items; through the feed only the latest read lands, and none of a wall the panel left.
    private async Task ReadRangeAsync(int take)
    {
        var wallId = WallId;
        void Commit(IReadOnlyList<WallGalleryItem> read)
        {
            items.Clear();
            items.AddRange(read);
            hasMore = read.Count == take;
            error = null;
        }

        try
        {
            if (feed is { } f)
            {
                await f.LoadAsync(() => ImageService.GetGalleryAsync(wallId, 0, take), Commit);
            }
            else
            {
                Commit(await ImageService.GetGalleryAsync(wallId, 0, take));
            }
        }
        catch (Exception ex)
        {
            error = $"Could not load the gallery: {ex.Message}";
            hasMore = false;
        }
    }
}
