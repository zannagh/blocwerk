// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Web.State;
using Blocwerk.Web.State.Live;
using Microsoft.AspNetCore.Components;

namespace Blocwerk.Web.Components.Pages.Walls;

/// <summary>
/// The live side of the 3D page (design §5.5): a <c>view3d.changed</c> (another wall model was activated) or a wall
/// resync rebuilds the view, and the new view instance remounts the stage. Held while a correction is picked or saving:
/// the correction's own rebuild then reads the newest model, and what arrived meanwhile is applied when it ends.
/// </summary>
public partial class Wall3D
{
    private static readonly IReadOnlySet<string> Kinds = new HashSet<string> { FeedKinds.View3DChanged };

    private LeafFeed? feed;

    [Inject]
    private WallCacheState Cache { get; set; } = null!;

    public void Dispose() => feed?.Dispose();

    private void OpenFeed()
    {
        if (feed?.Scope.WallId == WallId)
        {
            return;
        }

        feed?.Dispose();
        feed = LeafFeed.Open(Cache, new LeafScope(WallId, null, Kinds), InvokeAsync, ApplyFeedAsync, () => correcting || tappedFacet is not null);
    }

    private Task<T> BuildTaggedAsync<T>(Func<Task<T>> build) => feed is { } f ? f.LoadAsync(build) : build();

    private Task ReleaseFeedAsync() => feed?.ReleaseAsync() ?? Task.CompletedTask;

    // On the dispatcher. Every batch of this scope means "the model may have changed": rebuild.
    private async Task ApplyFeedAsync(LeafBatch batch)
    {
        if (_result is null || Cache.ViewerOf(WallId) is null)
        {
            return; // the first build is still running, or the circuit left the wall (the layout navigates away)
        }

        await ReloadAsync();
        StateHasChanged();
    }
}
