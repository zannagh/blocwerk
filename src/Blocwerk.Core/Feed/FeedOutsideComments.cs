// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Assets.DataDocs;
using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Feed;

/// <summary>
/// Comment rows as a viewer outside the wall (share link, kiosk) may receive them, decided at delivery: a row's audience
/// is fixed when it is written, but its boulder can be taken back to draft or archived afterwards, and a replay from an
/// old cursor must not then hand out the comment's text. The class rules are the comments document's
/// (<see cref="DocumentAudiences.SeesDrafts"/>, <see cref="DocumentAudiences.SeesArchived"/>), so the stream replayed after
/// a document never carries a comment the document leaves out.
/// </summary>
internal static class FeedOutsideComments
{
    /// <summary>True for <c>comment.added</c> and <c>comment.deleted</c>.</summary>
    public static bool IsComment(string kind) => kind is FeedKinds.CommentAdded or FeedKinds.CommentDeleted;

    /// <summary>
    /// False for a comment row whose boulder <paramref name="viewer"/>'s class may not see now: a draft or an archived
    /// boulder, for a viewer outside the wall. A boulder that is gone hides nothing: its comments are gone too.
    /// </summary>
    public static bool Allows(FeedViewer viewer, WallChange row, FeedHydrated hydrated)
    {
        if (!IsComment(row.Kind) || row.BoulderId is not { } boulderId || !hydrated.CommentBoulders.TryGetValue(boulderId, out var state))
        {
            return true;
        }

        var audience = DocumentAudiences.Of(viewer);
        return (!state.IsDraft || DocumentAudiences.SeesDrafts(audience)) && (!state.IsArchived || DocumentAudiences.SeesArchived(audience));
    }
}
