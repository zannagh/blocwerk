// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Feed;

namespace Blocwerk.Web.Endpoints.Feed;

/// <summary>
/// Classifies live events by what they mean for the stream's grant, which was decided once at connect. The hub's
/// subscription keeps the viewer it was opened with, so a change of role, owner or share token must end or re-check the
/// stream rather than keep delivering under the old audiences (PR 1.4's circuit rules, for a stream).
/// </summary>
internal static class FeedStreamChecks
{
    public static FeedStreamCheck Of(FeedEnvelope envelope, FeedViewer viewer) => envelope.Kind switch
    {
        FeedKinds.AccessRevoked => FeedStreamCheck.Revoked,
        FeedKinds.MemberChanged when viewer.UserId is { } user && envelope.EntityId == user => FeedStreamCheck.Recheck,

        // The owner moved, the share token rotated, or the wall was deleted or rebuilt in bulk: none of these carries the
        // caller's id (Wall rows name no user to outsiders), so every viewer decides again. Rare, and one query each.
        FeedKinds.WallUpdated or FeedKinds.WallResync => FeedStreamCheck.Recheck,
        _ => FeedStreamCheck.None,
    };

    public static FeedStreamCheck Max(FeedStreamCheck a, FeedStreamCheck b) => a >= b ? a : b;
}
