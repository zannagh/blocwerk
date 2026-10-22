// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Documents;
using Blocwerk.Core.Data;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Assets.DataDocs;

/// <summary>
/// Builds the <c>holds</c> document: the wall's live holds (<see cref="LiveWallHolds.Query"/>), the same for every
/// class. Holds are wall geometry, not boulders, so no class's set differs; what the update procedure keeps on a hold
/// (detection, review, staged generations) is left out.
/// </summary>
internal static class HoldsDocumentBuilder
{
    public static async Task<HoldsDocument?> BuildAsync(BlocwerkDbContext db, Guid wallId, CancellationToken ct)
    {
        var generation = await db.Walls.IgnoreQueryFilters().AsNoTracking()
            .Where(w => w.Id == wallId)
            .Select(w => (int?)w.CurrentGeneration)
            .FirstOrDefaultAsync(ct);
        if (generation is null)
        {
            return null;
        }

        // The generation is read once, above: a wall deleted from here on gives an empty read and the check below, not
        // QueryAsync's "Wall not found" (a 500 on a route a share link reaches).
        var panels = await LiveWallHolds.LoadPanelIdsAsync(db, wallId, ct);
        var holds = await LiveWallHolds.Query(db, wallId, generation.Value, panels).AsNoTracking()
            .Select(h => new
            {
                h.Id, h.WallPanelId, h.Generation, h.X, h.Y, h.Radius, h.Color, h.Category, h.HandType, h.IsOnKickboard,
                h.IsVirtual, Kept = h.KeptUnconfirmedGeneration != null, h.StackedOnHoldId, h.ShapePoints,
                h.ShapeControlPoints, h.ShapeHoles,
            })
            .ToListAsync(ct);

        if (!await db.Walls.IgnoreQueryFilters().AnyAsync(w => w.Id == wallId, ct))
        {
            return null;
        }

        var documented = holds.OrderBy(h => h.Id).Select(h => new HoldsDocumentHold(
            h.Id,
            h.WallPanelId,
            h.Generation,
            h.X,
            h.Y,
            h.Radius,
            h.Color,
            h.Category.ToString(),
            h.HandType?.ToString(),
            h.IsOnKickboard,
            h.IsVirtual,
            h.Kept,
            h.StackedOnHoldId,
            DocumentPoints.From(h.ShapePoints),
            DocumentPoints.From(h.ShapeControlPoints),
            DocumentPoints.Rings(h.ShapeHoles))).ToList();
        return new HoldsDocument(DataDocumentSchema.Version, wallId, generation.Value, documented);
    }
}
