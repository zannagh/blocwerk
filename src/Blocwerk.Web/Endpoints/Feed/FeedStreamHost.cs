// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Feed;

namespace Blocwerk.Web.Endpoints.Feed;

/// <summary>
/// What every feed stream shares: the hub it subscribes to, the reads it catches up with, its limits, and the app's
/// shutdown, at which every open stream says goodbye so a deploy is not held up by connections that would live minutes.
/// </summary>
public sealed class FeedStreamHost(
    IWallFeedHub hub,
    IWallFeed feed,
    FeedCatchUp catchUp,
    FeedStreamOptions options,
    FeedStreamLimits limits,
    IHostApplicationLifetime? lifetime = null)
{
    public IWallFeedHub Hub => hub;

    public IWallFeed Feed => feed;

    public FeedCatchUp CatchUp => catchUp;

    public FeedStreamOptions Options => options;

    public FeedStreamLimits Limits => limits;

    /// <summary>Gets the token cancelled when the app begins to stop.</summary>
    public CancellationToken Stopping => lifetime?.ApplicationStopping ?? CancellationToken.None;
}
