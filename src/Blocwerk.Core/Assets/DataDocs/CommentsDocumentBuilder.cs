// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Documents;
using Blocwerk.Core.Data;
using Blocwerk.Core.Feed;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Assets.DataDocs;

/// <summary>
/// Builds the <c>comments</c> document for one class: the full text of every comment on every boulder the class's
/// <c>boulders</c> document lists: historic ones for every class; drafts' and archived boulders' comments only for the
/// wall's members and setters (<see cref="DocumentAudiences.SeesDrafts"/>, <see cref="DocumentAudiences.SeesArchived"/>).
/// For share links and the kiosk that is stricter than <c>CommentService</c>'s read gate (#180), which keeps them off
/// drafts only and still shows an archived boulder's comments to a share link that opens it by id. Authors are described
/// by the feed's rule for the class (<see cref="DocumentAudiences.Authors"/>), so an erased, deleted or Ghost author reads
/// as the placeholder, exactly as the <c>comment.added</c> envelope replayed after the document shows them, and the kiosk
/// class names only the members who consented to the kiosk (<see cref="KioskBylines"/>), the others reading as
/// <see cref="KioskBylines.PlaceholderName"/>; whom it named is returned too, so a consent given or withdrawn since
/// rebuilds it (<see cref="DataDocumentFreshness"/>). A deleted
/// comment or boulder is gone from the rows, so it is gone from the next sha. The authors are returned beside the document
/// (never in it), so a rename or a new avatar of one of them rebuilds it, whichever wall the <c>user.profile</c> row was
/// written for.
/// </summary>
internal static class CommentsDocumentBuilder
{
    /// <summary>
    /// The document, the people it names (its authors), and for the kiosk class the authors it named by their consent
    /// (null for every other class), or null when the wall is gone.
    /// </summary>
    public static async Task<(CommentsDocument Document, IReadOnlyCollection<Guid> People, IReadOnlyCollection<Guid>? Consented)?> BuildAsync(
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
        var consented = visibility.MasksUnnamed ? await ConsentedAsync(db, wallId, people, ct) : null;

        var comments = rows.OrderBy(c => c.BoulderId).ThenBy(c => c.CreatedAt).ThenBy(c => c.Id)
            .Select(c => new CommentsDocumentComment(
                c.Id,
                c.BoulderId,
                visibility.Describe(authors.GetValueOrDefault(c.AuthorId), consented?.Contains(c.AuthorId) ?? true),
                c.Text,
                c.CreatedAt))
            .ToList();
        return (new CommentsDocument(DataDocumentSchema.Version, wallId, comments), people, consented);
    }

    /// <summary>Those of <paramref name="people"/> who consented to the kiosk on <paramref name="wallId"/> now.</summary>
    internal static async Task<IReadOnlySet<Guid>> ConsentedAsync(
        BlocwerkDbContext db, Guid wallId, IReadOnlyCollection<Guid> people, CancellationToken ct)
    {
        if (people.Count == 0)
        {
            return new HashSet<Guid>();
        }

        var ids = await db.WallMembers.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.WallId == wallId && people.Contains(m.UserId) && m.KioskConsentedAt != null)
            .Select(m => m.UserId)
            .ToListAsync(ct);
        return ids.ToHashSet();
    }
}
