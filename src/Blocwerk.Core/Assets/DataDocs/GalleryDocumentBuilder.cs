// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Documents;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Assets.DataDocs;

/// <summary>
/// Builds the <c>gallery</c> document: every class sees the gallery (the manifest lists it for every class too). A
/// stored name that escapes the image store is left out, as the manifest and the resolver leave it out.
/// </summary>
internal static class GalleryDocumentBuilder
{
    public static async Task<GalleryDocument?> BuildAsync(BlocwerkDbContext db, IWallImageStorage storage, Guid wallId, CancellationToken ct)
    {
        if (!await db.Walls.IgnoreQueryFilters().AnyAsync(w => w.Id == wallId, ct))
        {
            return null;
        }

        var rows = await db.WallImages.AsNoTracking()
            .Where(i => i.WallId == wallId)
            .Select(i => new { i.Id, i.StoragePath, i.ContentType, i.SizeBytes, i.Sha256, i.Caption, i.CapturedAt })
            .ToListAsync(ct);
        var images = rows.Where(i => storage.ResolvePhysicalPath(i.StoragePath) is not null)
            .OrderByDescending(i => i.CapturedAt).ThenBy(i => i.Id)
            .Select(i => new GalleryDocumentImage(i.Id, ContentHash.ToHex(i.Sha256), i.ContentType, i.SizeBytes, i.Caption, i.CapturedAt))
            .ToList();
        return new GalleryDocument(DataDocumentSchema.Version, wallId, images);
    }
}
