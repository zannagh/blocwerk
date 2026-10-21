// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Feed;

/// <summary>The outcome of <see cref="FeedAccessGate"/>: the viewer to read as, or why there is none.</summary>
/// <param name="Viewer">The viewer, null when refused.</param>
/// <param name="Refusal">Why it was refused; <see cref="FeedAccessRefusal.None"/> when granted.</param>
/// <param name="CallerUserId">
/// The signed-in user behind the request, whatever the viewer: a non-member reading through a share token is
/// <see cref="FeedViewer.Anonymous"/> but still counted as themselves (stream caps). Null for nobody and for a kiosk.
/// </param>
public sealed record FeedAccess(FeedViewer? Viewer, FeedAccessRefusal Refusal, Guid? CallerUserId = null)
{
    public static FeedAccess Grant(FeedViewer viewer, Guid? callerUserId) => new(viewer, FeedAccessRefusal.None, callerUserId);

    public static FeedAccess Refuse(FeedAccessRefusal refusal) => new(null, refusal);
}
