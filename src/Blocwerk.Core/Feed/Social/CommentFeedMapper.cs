// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Contracts.Feed.Payloads;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Blocwerk.Core.Feed.Social;

/// <summary>
/// <c>comment.added</c> / <c>comment.deleted</c>, credited to the comment's author (hydration shows their byline through
/// <see cref="AuthorVisibility"/>; the text is read by id at delivery and never stored). A deletion also deletes the
/// stored <c>comment.added</c> rows in the same save (<see cref="FeedCommentCompaction"/>). Comments are not edited in
/// place, so a modification maps to nothing.
/// </summary>
internal sealed class CommentFeedMapper : IFeedMapper
{
    public IReadOnlyCollection<Type> EntityTypes { get; } = [typeof(BoulderComment)];

    public void Map(EntityEntry entry, FeedMapContext context)
    {
        var kind = entry.State switch
        {
            EntityState.Added => FeedKinds.CommentAdded,
            EntityState.Deleted => FeedKinds.CommentDeleted,
            _ => null,
        };
        if (kind is null)
        {
            return;
        }

        var comment = (BoulderComment)entry.Entity;
        var row = SocialRows.OnBoulder(comment.BoulderId, kind, comment.Id, new CommentPayload(comment.BoulderId));
        row.ActorUserId = comment.UserId;
        context.Add(row);
    }
}
