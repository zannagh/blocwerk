// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Documents;
using Blocwerk.Core.Data;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Assets.DataDocs;

/// <summary>
/// Builds the <c>wall</c> document: the same for every class. Metadata only (no photo bytes, no owner, no share token,
/// nothing staged), and the panels are the live ones (<see cref="LiveWallHolds.LoadPanelIdsAsync"/>).
/// </summary>
internal static class WallDocumentBuilder
{
    private const string Jpeg = "image/jpeg";

    public static async Task<WallDocument?> BuildAsync(BlocwerkDbContext db, Guid wallId, CancellationToken ct)
    {
        var wall = await db.Walls.IgnoreQueryFilters().AsNoTracking()
            .Where(w => w.Id == wallId)
            .Select(w => new { w.Name, w.Description, w.Angle, w.CurrentGeneration, HasPhoto = w.Photo != null, w.PhotoSha256, w.BorderPoints })
            .FirstOrDefaultAsync(ct);
        if (wall is null)
        {
            return null;
        }

        var segments = await db.WallSegments.AsNoTracking().Where(s => s.WallId == wallId).ToListAsync(ct);
        var live = await LiveWallHolds.LoadPanelIdsAsync(db, wallId, ct);
        var panels = await db.WallPanels.AsNoTracking()
            .Where(p => live.Contains(p.Id))
            .Select(p => new { p.Id, p.Col, p.Row, p.Generation, p.PhotoSha256, p.PhotoContentType })
            .ToListAsync(ct);

        return new WallDocument(
            DataDocumentSchema.Version,
            wallId,
            wall.Name,
            wall.Description,
            wall.Angle,
            wall.CurrentGeneration,
            wall.HasPhoto ? ContentHash.ToHex(wall.PhotoSha256) : null,
            DocumentPoints.From(wall.BorderPoints),
            segments.OrderBy(s => s.SortOrder).ThenBy(s => s.Id)
                .Select(s => new WallDocumentSegment(s.Id, s.Name, s.Kind.ToString(), s.Angle, s.Yaw, s.SortOrder, DocumentPoints.From(s.Points)!))
                .ToList(),
            panels.OrderBy(p => p.Row).ThenBy(p => p.Col).ThenBy(p => p.Id)
                .Select(p => new WallDocumentPanel(p.Id, p.Col, p.Row, p.Generation, ContentHash.ToHex(p.PhotoSha256), p.PhotoContentType ?? Jpeg))
                .ToList());
    }
}
