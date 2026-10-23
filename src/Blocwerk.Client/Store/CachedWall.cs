// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Documents;

namespace Blocwerk.Client.Store;

/// <summary>
/// One wall's cached data, immutable: every change (a replaced document, a feed delta) is a new instance that the store
/// swaps in as a whole, so a reader never sees half of one. The documents are the server's per-class documents and
/// are never widened here; the deltas only ever narrow them until the next document replaces them.
/// </summary>
public sealed record CachedWall
{
    /// <summary>Gets the wall.</summary>
    public required Guid WallId { get; init; }

    /// <summary>Gets the class the documents were built for (decides the outsider purge).</summary>
    public required StoreAudience Audience { get; init; }

    /// <summary>Gets the wall document (meta, segments, panels), or null when it is not cached.</summary>
    public WallDocument? Wall { get; init; }

    /// <summary>Gets the holds document, or null.</summary>
    public HoldsDocument? Holds { get; init; }

    /// <summary>Gets the boulders document, or null.</summary>
    public BouldersDocument? Boulders { get; init; }

    /// <summary>Gets the gallery document, or null.</summary>
    public GalleryDocument? Gallery { get; init; }

    /// <summary>Gets every cached comment of the wall; read them through <see cref="CommentsFor"/>.</summary>
    public IReadOnlyList<CommentsDocumentComment> Comments { get; init; } = [];

    /// <summary>Gets the sha of each document kind the instance holds.</summary>
    public IReadOnlyDictionary<string, string> Shas { get; init; } = new Dictionary<string, string>();

    /// <summary>Gets the viewer's own marks (none unless the device is signed in).</summary>
    public MineMarks Mine { get; init; } = MineMarks.None;

    /// <summary>Gets the boulders the class may see.</summary>
    public IReadOnlyList<BouldersDocumentBoulder> VisibleBoulders => Boulders?.Boulders ?? [];

    /// <summary>True when the boulders document lists the boulder.</summary>
    /// <param name="boulderId">The boulder.</param>
    /// <returns>Whether the class sees it.</returns>
    public bool HasBoulder(Guid boulderId) => VisibleBoulders.Any(b => b.Id == boulderId);

    /// <summary>
    /// A boulder's comments, oldest first; none for a boulder the boulders document does not list, so a comments
    /// document that lags behind a boulder's move to draft never shows it.
    /// </summary>
    /// <param name="boulderId">The boulder.</param>
    /// <returns>Its comments.</returns>
    public IReadOnlyList<CommentsDocumentComment> CommentsFor(Guid boulderId) => HasBoulder(boulderId)
        ? Comments.Where(c => c.BoulderId == boulderId).OrderBy(c => c.CreatedAt).ThenBy(c => c.Id).ToList()
        : [];

    /// <summary>
    /// The wall with one document replaced by a new sha. The new document is authoritative: a comments document swaps
    /// the whole set (deletions and anonymised authors included), never merged over the old one. A document that does
    /// not parse, or names another wall, leaves the wall as it was.
    /// </summary>
    /// <param name="kind">The document kind.</param>
    /// <param name="sha">Its sha.</param>
    /// <param name="json">Its JSON.</param>
    /// <returns>The new instance, or this one.</returns>
    public CachedWall WithDocument(string kind, string sha, string json)
    {
        var parsed = DataDocumentReader.Read(kind, json, WallId);
        if (parsed is null)
        {
            return this;
        }

        var shas = new Dictionary<string, string>(Shas) { [kind] = sha };
        return parsed switch
        {
            WallDocument wall => this with { Wall = wall, Shas = shas },
            HoldsDocument holds => this with { Holds = holds, Shas = shas },
            BouldersDocument boulders => this with { Boulders = boulders, Shas = shas },
            GalleryDocument gallery => this with { Gallery = gallery, Shas = shas },
            CommentsDocument comments => this with { Comments = comments.Comments, Shas = shas },
            _ => this,
        };
    }

    /// <summary>A <c>comment.added</c>: the comment joins, once per id (a replayed event is no second copy).</summary>
    /// <param name="comment">The comment.</param>
    /// <returns>The new instance, or this one.</returns>
    public CachedWall WithCommentAdded(CommentsDocumentComment comment) => Comments.Any(c => c.Id == comment.Id)
        ? this
        : this with { Comments = [.. Comments, comment] };

    /// <summary>A <c>comment.deleted</c>: the comment goes.</summary>
    /// <param name="commentId">The comment.</param>
    /// <returns>The new instance, or this one.</returns>
    public CachedWall WithCommentDeleted(Guid commentId) => Comments.Any(c => c.Id == commentId)
        ? this with { Comments = Comments.Where(c => c.Id != commentId).ToList() }
        : this;

    /// <summary>A <c>boulder.deleted</c>: the boulder and its comments go, for every class.</summary>
    /// <param name="boulderId">The boulder.</param>
    /// <returns>The new instance.</returns>
    public CachedWall WithBoulderDeleted(Guid boulderId) => WithoutBoulder(boulderId);

    /// <summary>
    /// A <c>boulder.changed</c> with <c>isDraft: true</c>. An outsider class (a share link, a kiosk) may not see a draft:
    /// its comments go at once, before the debounced index refetch, because such a device never receives the
    /// <c>comment.deleted</c> rows of a boulder that has gone to draft; the boulder itself goes too. A member's class
    /// sees drafts and keeps both.
    /// </summary>
    /// <param name="boulderId">The boulder.</param>
    /// <returns>The new instance, or this one.</returns>
    public CachedWall WithBoulderDrafted(Guid boulderId) => Audience == StoreAudience.Member ? this : WithoutBoulder(boulderId);

    private CachedWall WithoutBoulder(Guid boulderId) => this with
    {
        Comments = Comments.Where(c => c.BoulderId != boulderId).ToList(),
        Boulders = Boulders is null ? null : Boulders with { Boulders = Boulders.Boulders.Where(b => b.Id != boulderId).ToList() },
    };
}
