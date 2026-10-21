// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Feed;

/// <summary>The outcome of <see cref="FeedAccessGate"/>: the viewer to read as, or why there is none.</summary>
/// <param name="Viewer">The viewer, null when refused.</param>
/// <param name="Refusal">Why it was refused; <see cref="FeedAccessRefusal.None"/> when granted.</param>
public sealed record FeedAccess(FeedViewer? Viewer, FeedAccessRefusal Refusal)
{
    public static FeedAccess Grant(FeedViewer viewer) => new(viewer, FeedAccessRefusal.None);

    public static FeedAccess Refuse(FeedAccessRefusal refusal) => new(null, refusal);
}
