// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Documents;
using Blocwerk.Core.Data;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Assets.DataDocs;

/// <summary>
/// Builds the <c>boulders</c> document for one class: the wall's active boulders (not archived, not historic), drafts
/// only for the wall's members and setters (<see cref="DocumentAudiences.SeesDrafts"/>), and drafts' setters only
/// where drafts are. The byline is the one <see cref="BoulderSetterNames.Describe"/> renders on the pages, so an erased
/// or Ghost creator reads as the placeholder; no user id is written. Stats are wall-wide counts
/// (<see cref="BoulderStatsCounts"/>), never the reader's own marks.
/// </summary>
internal static class BouldersDocumentBuilder
{
    public static async Task<BouldersDocument?> BuildAsync(BlocwerkDbContext db, Guid wallId, DocumentAudience audience, CancellationToken ct)
    {
        if (!await db.Walls.IgnoreQueryFilters().AnyAsync(w => w.Id == wallId, ct))
        {
            return null;
        }

        var drafts = DocumentAudiences.SeesDrafts(audience);
        var boulders = await db.Boulders.AsNoTracking()
            .Where(b => b.WallId == wallId && !b.IsArchived && !b.IsHistoric && (drafts || !b.IsDraft))
            .Select(b => new BoulderRow(
                b.Id, b.Name, b.Grade, b.Generation, b.IsDraft, b.CreatedAt, b.PublishedAt, b.NoMatch, b.KickboardFootholdsOn,
                b.HandsFollowFeet, b.FootColorOnly, b.CreatedByUserId, b.CreatedBy.DisplayName, b.CreatedBy.CustomDisplayName))
            .ToListAsync(ct);
        var ids = boulders.Select(b => b.Id).ToList();
        var holds = (await db.BoulderHolds.AsNoTracking()
                .Where(h => ids.Contains(h.BoulderId))
                .Select(h => new { h.BoulderId, h.HoldId, h.Type, h.Usage })
                .ToListAsync(ct))
            .ToLookup(h => h.BoulderId, h => new BouldersDocumentHold(h.HoldId, h.Type.ToString(), h.Usage.ToString()));
        var setters = await BoulderSetterNames.LoadForWallAsync(db, wallId, includeDrafts: drafts);
        var stats = await BoulderStatsCounts.LoadAsync(db, ids, ct);

        var documented = boulders.OrderBy(b => b.Id).Select(b => new BouldersDocumentBoulder(
            b.Id,
            b.Name,
            b.Grade,
            b.Generation,
            b.IsDraft ? true : null,
            b.CreatedAt,
            b.PublishedAt,
            BoulderSetterNames.Describe(setters.GetValueOrDefault(b.Id), b.Creator()),
            b.NoMatch,
            b.KickboardFootholdsOn,
            b.HandsFollowFeet,
            b.FootColorOnly,
            stats.GetValueOrDefault(b.Id) ?? BoulderStatsCounts.None,
            holds[b.Id].OrderBy(h => h.HoldId).ToList())).ToList();
        return new BouldersDocument(DataDocumentSchema.Version, wallId, documented);
    }
}
