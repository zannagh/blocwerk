// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;

namespace Blocwerk.Core.Feed;

/// <summary>What one hub read loaded from the source tables for its rows: authors, comment content, and its boulders.</summary>
public sealed class FeedHydrated
{
    public static readonly FeedHydrated Empty = new(new Dictionary<Guid, FeedAuthor>(), new Dictionary<Guid, FeedContent>());

    public FeedHydrated(
        IReadOnlyDictionary<Guid, FeedAuthor> authors,
        IReadOnlyDictionary<Guid, FeedContent> content,
        IReadOnlyDictionary<Guid, FeedBoulderState>? commentBoulders = null)
    {
        Authors = authors;
        Content = content;
        CommentBoulders = commentBoulders ?? new Dictionary<Guid, FeedBoulderState>();
    }

    /// <summary>Gets the user rows by id; an id missing here has no row any more.</summary>
    public IReadOnlyDictionary<Guid, FeedAuthor> Authors { get; }

    /// <summary>Gets the content by entity id; an id missing here is a tombstone.</summary>
    public IReadOnlyDictionary<Guid, FeedContent> Content { get; }

    /// <summary>
    /// Gets the current state of the boulders the read's comment rows are on, by boulder id; a boulder missing here is
    /// gone. Read at delivery, so a boulder taken back to draft or archived since the row was written is seen as it is now.
    /// </summary>
    public IReadOnlyDictionary<Guid, FeedBoulderState> CommentBoulders { get; }
}
