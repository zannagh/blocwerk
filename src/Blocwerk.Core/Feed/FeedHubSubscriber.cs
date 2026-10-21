// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Feed;

/// <summary>One subscriber of a hub wall, with the visibility rule its viewer gets.</summary>
internal sealed class FeedHubSubscriber
{
    public FeedHubSubscriber(FeedViewer viewer, IFeedSubscriber target)
    {
        Viewer = viewer;
        Target = target;
        Visibility = AuthorVisibility.For(viewer);
    }

    public FeedViewer Viewer { get; }

    public IFeedSubscriber Target { get; }

    public AuthorVisibility Visibility { get; }
}
