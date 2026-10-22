// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Feed;

namespace Blocwerk.Web.State.Live;

/// <summary>
/// The byline of a list item loaded from the database, through the same <see cref="AuthorVisibility"/> rule the feed's
/// hydration applies, so a row read by a query and the same row patched in from an event render identically: an erased,
/// missing or system author shows as Ghost, and a viewer outside the wall gets an author id only with an avatar.
/// </summary>
public static class LeafAuthors
{
    /// <summary>
    /// The author as <paramref name="viewer"/> sees them; a circuit without a subscription to the wall (a share link) is
    /// an outsider.
    /// </summary>
    public static FeedActor Describe(User? user, FeedViewer? viewer)
    {
        var author = user is null ? null : new FeedAuthor(user.Id, user.Name, user.HasAvatar, user.IsDeleted);
        return AuthorVisibility.For(viewer ?? FeedViewer.Anonymous()).Describe(author);
    }
}
