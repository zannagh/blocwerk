// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Web.State;
using Blocwerk.Web.State.Live;
using Microsoft.AspNetCore.Components;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// The live side of the beta strip (design §5.5). A <c>video.deleted</c> (or a tombstone) drops the tile by id. An upload
/// or an encode step re-reads the strip through the viewer's own query: the envelope carries the status but not the
/// uploader's byline or the poster, and the status of two transactions may arrive out of order, so it is never applied
/// from the payload. The clip open in the lightbox keeps its own copy, so a reload never swaps the player under the viewer.
/// </summary>
public partial class BoulderBetaVideos
{
    private static readonly IReadOnlySet<string> Kinds = new HashSet<string>
    {
        FeedKinds.VideoAdded, FeedKinds.VideoStatus, FeedKinds.VideoDeleted, FeedKinds.UserProfile,
    };

    private static readonly IReadOnlySet<string> Removals = new HashSet<string> { FeedKinds.VideoDeleted };
    private static readonly IReadOnlySet<string> NoUpserts = new HashSet<string>();

    private LeafFeed? _feed;
    private (Guid Wall, Guid Boulder)? _feedKey;

    /// <summary>The boulder's wall, whose feed keeps the strip live.</summary>
    [Parameter]
    public Guid WallId { get; set; }

    [Inject]
    private WallCacheState Cache { get; set; } = null!;

    protected override Task OnParametersSetAsync()
    {
        if (_feedKey == (WallId, BoulderId))
        {
            return Task.CompletedTask;
        }

        _feedKey = (WallId, BoulderId);
        _feed?.Dispose();
        _feed = WallId == Guid.Empty
            ? null
            : LeafFeed.Open(Cache, new LeafScope(WallId, BoulderId, Kinds), InvokeAsync, ApplyFeedAsync);
        return LoadAsync();
    }

    private Task<List<Core.Services.BetaVideoInfo>> ReadTaggedAsync(Func<Task<List<Core.Services.BetaVideoInfo>>> read) =>
        _feed is { } feed ? feed.LoadAsync(read) : read();

    // On the dispatcher.
    private async Task ApplyFeedAsync(LeafBatch batch)
    {
        var plan = LeafPlan.Of(batch, NoUpserts, Removals);
        if (plan.Reload || (_videos?.Any(v => plan.Profiles.Contains(v.UploadedByUserId)) ?? false))
        {
            await LoadAsync();
        }
        else
        {
            _videos?.RemoveAll(v => plan.Removals.Contains(v.Id));
        }

        StateHasChanged();
    }
}
