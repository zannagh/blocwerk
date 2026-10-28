// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;

namespace Blocwerk.Core.Feed;

/// <summary>
/// What one hub read loaded from the source tables for its rows: authors and comment content, by id, its comment rows'
/// boulders, and what the kiosk tablet's delivery needs of them (<see cref="FeedKioskFacts"/>).
/// </summary>
public sealed class FeedHydrated
{
    public static readonly FeedHydrated Empty = new(new Dictionary<Guid, FeedAuthor>(), new Dictionary<Guid, FeedContent>());

    public FeedHydrated(
        IReadOnlyDictionary<Guid, FeedAuthor> authors,
        IReadOnlyDictionary<Guid, FeedContent> content,
        IReadOnlyDictionary<Guid, FeedBoulderState>? commentBoulders = null,
        FeedKioskFacts? kiosk = null)
    {
        Authors = authors;
        Content = content;
        CommentBoulders = commentBoulders ?? new Dictionary<Guid, FeedBoulderState>();
        Kiosk = kiosk ?? FeedKioskFacts.None;
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

    /// <summary>Gets what the kiosk tablet's delivery needs of the read's rows (<see cref="FeedKioskActivity"/>, bylines).</summary>
    public FeedKioskFacts Kiosk { get; }
}
