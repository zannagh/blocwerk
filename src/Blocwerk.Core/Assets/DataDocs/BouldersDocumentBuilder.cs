// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Documents;
using Blocwerk.Core.Data;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Assets.DataDocs;

/// <summary>
/// Builds the <c>boulders</c> document for one class: the wall's boulders as the wall page lists them, historic ones
/// included; drafts and archived boulders, and drafts' setters, only for the wall's members and setters
/// (<see cref="DocumentAudiences.SeesDrafts"/>, <see cref="DocumentAudiences.SeesArchived"/>). The byline is the one
/// <see cref="BoulderSetterNames.Describe"/> renders on the pages, so an erased or Ghost creator reads as the placeholder;
/// no user id is written. The people the bylines name are returned beside the document (never in it), so a rename of a
/// setter who left the wall still rebuilds it. Stats are wall-wide counts (<see cref="BoulderStatsCounts"/>), never the
/// reader's own marks, and only for the classes that see them online (<see cref="DocumentAudiences.SeesStats"/>); the
/// others get <see cref="BoulderStatsCounts.None"/>.
/// </summary>
internal static class BouldersDocumentBuilder
{
    /// <summary>The document, and the people its bylines name (creators and setters), or null when the wall is gone.</summary>
    public static async Task<(BouldersDocument Document, IReadOnlyCollection<Guid> People)?> BuildAsync(
        BlocwerkDbContext db, Guid wallId, DocumentAudience audience, CancellationToken ct)
    {
        if (!await db.Walls.IgnoreQueryFilters().AnyAsync(w => w.Id == wallId, ct))
        {
            return null;
        }

        var drafts = DocumentAudiences.SeesDrafts(audience);
        var archived = DocumentAudiences.SeesArchived(audience);
        var boulders = await db.Boulders.AsNoTracking()
            .Where(b => b.WallId == wallId && (archived || !b.IsArchived) && (drafts || !b.IsDraft))
            .Select(b => new BoulderRow(
                b.Id, b.Name, b.Grade, b.Generation, b.IsDraft, b.IsHistoric, b.IsArchived, b.CreatedAt, b.PublishedAt,
                b.NoMatch, b.KickboardFootholdsOn, b.HandsFollowFeet, b.FootColorOnly, b.CreatedByUserId,
                b.CreatedBy.DisplayName, b.CreatedBy.CustomDisplayName))
            .ToListAsync(ct);
        var ids = boulders.Select(b => b.Id).ToList();
        var holds = (await db.BoulderHolds.AsNoTracking()
                .Where(h => ids.Contains(h.BoulderId))
                .Select(h => new { h.BoulderId, h.HoldId, h.Type, h.Usage })
                .ToListAsync(ct))
            .ToLookup(h => h.BoulderId, h => new BouldersDocumentHold(h.HoldId, h.Type.ToString(), h.Usage.ToString()));
        var setters = await BoulderSetterNames.LoadForWallAsync(db, wallId, includeDrafts: drafts);
        var setterIds = await db.BoulderSetters.AsNoTracking().Where(s => ids.Contains(s.BoulderId)).Select(s => s.UserId).ToListAsync(ct);
        var stats = DocumentAudiences.SeesStats(audience) ? await BoulderStatsCounts.LoadAsync(db, ids, ct) : [];

        var documented = boulders.OrderBy(b => b.Id)
            .Select(b => Map(b, setters.GetValueOrDefault(b.Id), stats.GetValueOrDefault(b.Id), holds[b.Id]))
            .ToList();
        var people = boulders.Select(b => b.CreatorId).Concat(setterIds).ToHashSet();
        return (new BouldersDocument(DataDocumentSchema.Version, wallId, documented), people);
    }

    private static BouldersDocumentBoulder Map(
        BoulderRow b, List<string>? setterNames, BouldersDocumentStats? stats, IEnumerable<BouldersDocumentHold> holds) => new(
        b.Id,
        b.Name,
        b.Grade,
        b.Generation,
        b.IsDraft ? true : null,
        b.IsHistoric,
        b.IsArchived,
        b.CreatedAt,
        b.PublishedAt,
        BoulderSetterNames.Describe(setterNames, b.Creator()),
        b.NoMatch,
        b.KickboardFootholdsOn,
        b.HandsFollowFeet,
        b.FootColorOnly,
        stats ?? BoulderStatsCounts.None,
        holds.OrderBy(h => h.HoldId).ToList());
}
