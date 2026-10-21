// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Feed;

namespace Blocwerk.Web.State.Live;

/// <summary>One comment as the live list renders it, read by a query or patched in from a hydrated event.</summary>
/// <param name="Id">The comment id.</param>
/// <param name="OwnerId">The author's id where the viewer may know it (for the delete button), else null.</param>
/// <param name="Author">The byline, per <see cref="AuthorVisibility"/>.</param>
/// <param name="Text">The text.</param>
/// <param name="CreatedAt">When it was written.</param>
public sealed record CommentRow(Guid Id, Guid? OwnerId, FeedActor Author, string Text, DateTimeOffset CreatedAt)
{
    public static CommentRow Of(BoulderComment comment, FeedViewer? viewer)
    {
        var author = LeafAuthors.Describe(comment.User, viewer);
        return new CommentRow(comment.Id, comment.UserId, author, comment.Text, comment.CreatedAt);
    }

    /// <summary>The row of a hydrated <c>comment.added</c>; null when the event carries no text.</summary>
    public static CommentRow? Of(FeedEnvelope e)
    {
        if (e.EntityId is not { } id || e.Hydrated?.Text is not { } text)
        {
            return null;
        }

        var author = e.Actor ?? new FeedActor(null, Core.Helpers.PlaceholderIdentity.DisplayName, false, null);
        return new CommentRow(id, author.Id, author, text, e.Hydrated.CreatedAt ?? e.At);
    }
}
