// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Web.State.Feed;

/// <summary>What one feed event removed from a circuit's cache.</summary>
/// <param name="Wall">True when wall-level entries went (the aggregate, a hold set, or all of the wall).</param>
/// <param name="Boulders">The cached boulders that went.</param>
public sealed record WallFeedEvicted(bool Wall, IReadOnlyCollection<Guid> Boulders)
{
    public static WallFeedEvicted None { get; } = new(false, []);

    public bool Any => Wall || Boulders.Count > 0;

    public static WallFeedEvicted Whole(bool any) => any ? new(true, []) : None;
}
