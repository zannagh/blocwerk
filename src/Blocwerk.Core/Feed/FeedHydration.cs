// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Feed;

/// <summary>
/// Read-time hydration (design §5.4): the feed stores ids only, and the content a consumer shows is read from the source
/// tables when the rows are delivered, one batched query per kind of content and read, never per event. An erased user's
/// rows therefore hydrate from the scrubbed source rows, and a deleted comment hydrates as a tombstone.
/// </summary>
public static class FeedHydration
{
    /// <summary>True for kinds whose envelope carries content, and so becomes a tombstone when the source row is gone.</summary>
    public static bool HasContent(string kind) => kind == FeedKinds.CommentAdded;

    public static async Task<FeedHydrated> LoadAsync(BlocwerkDbContext db, IReadOnlyList<WallChange> rows, CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return FeedHydrated.Empty;
        }

        var authors = await AuthorsAsync(db, rows, cancellationToken);
        var content = await CommentsAsync(db, rows, cancellationToken);
        return new FeedHydrated(authors, content);
    }

    private static async Task<IReadOnlyDictionary<Guid, FeedAuthor>> AuthorsAsync(
        BlocwerkDbContext db, IReadOnlyList<WallChange> rows, CancellationToken cancellationToken)
    {
        var ids = rows.Where(r => r.ActorUserId is not null).Select(r => r.ActorUserId!.Value).Distinct().ToList();
        return await AuthorsAsync(db, ids, cancellationToken);
    }

    /// <summary>The users <paramref name="ids"/> as bylines read them; a user row that is gone is simply missing.</summary>
    internal static async Task<IReadOnlyDictionary<Guid, FeedAuthor>> AuthorsAsync(
        BlocwerkDbContext db, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return new Dictionary<Guid, FeedAuthor>();
        }

        var users = await db.Users.AsNoTracking()
            .Where(u => ids.Contains(u.Id))
            .Select(u => new { u.Id, u.CustomDisplayName, u.DisplayName, HasAvatar = u.AvatarImage != null && u.AvatarImage.Length > 0, u.DeletedAt })
            .ToListAsync(cancellationToken);
        return users.ToDictionary(
            u => u.Id,
            u => new FeedAuthor(u.Id, string.IsNullOrWhiteSpace(u.CustomDisplayName) ? u.DisplayName : u.CustomDisplayName, u.HasAvatar, u.DeletedAt != null));
    }

    private static async Task<IReadOnlyDictionary<Guid, FeedContent>> CommentsAsync(
        BlocwerkDbContext db, IReadOnlyList<WallChange> rows, CancellationToken cancellationToken)
    {
        var ids = rows.Where(r => r.Kind == FeedKinds.CommentAdded && r.EntityId is not null)
            .Select(r => r.EntityId!.Value).Distinct().ToList();
        if (ids.Count == 0)
        {
            return new Dictionary<Guid, FeedContent>();
        }

        var comments = await db.BoulderComments.AsNoTracking()
            .Where(c => ids.Contains(c.Id))
            .Select(c => new { c.Id, c.Text, c.CreatedAt })
            .ToListAsync(cancellationToken);
        return comments.ToDictionary(c => c.Id, c => new FeedContent(c.Text, c.CreatedAt));
    }
}
