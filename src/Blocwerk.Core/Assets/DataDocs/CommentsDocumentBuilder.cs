// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Documents;
using Blocwerk.Core.Data;
using Blocwerk.Core.Feed;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Assets.DataDocs;

/// <summary>
/// Builds the <c>comments</c> document for one class: the full text of every comment on every boulder the class's
/// <c>boulders</c> document lists: historic ones for every class; drafts' and archived boulders' comments only for the
/// wall's members and setters (<see cref="DocumentAudiences.SeesDrafts"/>, <see cref="DocumentAudiences.SeesArchived"/>),
/// within <c>CommentService</c>'s read gate (#180), which keeps share links and the kiosk off drafts. Authors are described by the feed's rule for the class
/// (<see cref="DocumentAudiences.Authors"/>), so an erased, deleted or Ghost author reads as the placeholder, exactly as the
/// <c>comment.added</c> envelope replayed after the document shows them. A deleted comment or boulder is gone from the
/// rows, so it is gone from the next sha. The authors are returned beside the document (never in it), so a rename or a
/// new avatar of one of them rebuilds it, whichever wall the <c>user.profile</c> row was written for.
/// </summary>
internal static class CommentsDocumentBuilder
{
    /// <summary>The document, and the people it names (its authors), or null when the wall is gone.</summary>
    public static async Task<(CommentsDocument Document, IReadOnlyCollection<Guid> People)?> BuildAsync(
        BlocwerkDbContext db, Guid wallId, DocumentAudience audience, CancellationToken ct)
    {
        if (!await db.Walls.IgnoreQueryFilters().AnyAsync(w => w.Id == wallId, ct))
        {
            return null;
        }

        var drafts = DocumentAudiences.SeesDrafts(audience);
        var archived = DocumentAudiences.SeesArchived(audience);
        var rows = await db.BoulderComments.AsNoTracking()
            .Where(c => c.Boulder.WallId == wallId && (drafts || !c.Boulder.IsDraft) && (archived || !c.Boulder.IsArchived))
            .Select(c => new { c.Id, c.BoulderId, AuthorId = c.UserId, c.Text, c.CreatedAt })
            .ToListAsync(ct);
        var people = rows.Select(c => c.AuthorId).ToHashSet();
        var authors = await FeedHydration.AuthorsAsync(db, people, ct);
        var visibility = DocumentAudiences.Authors(audience);

        var comments = rows.OrderBy(c => c.BoulderId).ThenBy(c => c.CreatedAt).ThenBy(c => c.Id)
            .Select(c => new CommentsDocumentComment(
                c.Id, c.BoulderId, visibility.Describe(authors.GetValueOrDefault(c.AuthorId)), c.Text, c.CreatedAt))
            .ToList();
        return (new CommentsDocument(DataDocumentSchema.Version, wallId, comments), people);
    }
}
