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
/// the correction's own rebuild then reads the newest model, and what arrived meanwhile is applied when it ends. A resync
/// or a changed viewer that yields the same active model keeps the mounted stage (camera, mode, WebGL context).
/// </summary>
public partial class Wall3D
{
    private static readonly IReadOnlySet<string> Kinds = new HashSet<string> { FeedKinds.View3DChanged };

    private LeafFeed? feed;

    [Inject]
    private WallCacheState Cache { get; set; } = null!;

    [Inject]
    private ILogger<Wall3D> Logger { get; set; } = null!;

    public void Dispose() => feed?.Dispose();

    // Share routes are not live, as on the wall and boulder pages.
    private void OpenFeed()
    {
        feed?.Dispose();
        feed = string.IsNullOrEmpty(ShareToken)
            ? LeafFeed.Open(Cache, new LeafScope(WallId, null, Kinds), InvokeAsync, ApplyFeedAsync, () => correcting || tappedFacet is not null, Logger)
            : null;
    }

    // Through the feed, a build that arrives meanwhile is held and applied after, and only the latest build lands.
    private async Task BuildTaggedAsync<T>(Func<Task<T>> build, Action<T> commit)
    {
        if (feed is { } f)
        {
            await f.LoadAsync(build, commit);
        }
        else
        {
            commit(await build());
        }
    }

    private Task ReleaseFeedAsync() => feed?.ReleaseAsync() ?? Task.CompletedTask;

    // On the dispatcher. The model may have changed: rebuild, keeping the stage when it did not.
    private async Task ApplyFeedAsync(LeafBatch batch)
    {
        if (Cache.ViewerOf(WallId) is null)
        {
            return; // the circuit left the wall; the layout navigates away
        }

        await ReloadAsync(keepSameModel: true);
        StateHasChanged();
    }
}
