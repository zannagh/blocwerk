using Blocwerk.Core.Data;
using Blocwerk.Core.Stitching;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Services;

/// <summary>
/// Builds the multipart payload for a <c>POST /jobs</c> call out of the wall's current state: the
/// live hold set for the wall's current generation, and the wall's current photo as the
/// <c>oldPhoto</c> part the matcher needs.
/// </summary>
internal static class WallStitchRequestBuilder
{
    public static async Task<StitchJobOptions> BuildOptionsAsync(
        BlocwerkDbContext db,
        Guid wallId,
        WallStitchStartOptions options,
        CancellationToken ct)
    {
        if (!options.TransferHolds)
        {
            return new StitchJobOptions(
                options.WallAngleDegrees,
                options.DefaultProjection.ToWire(),
                TransferHolds: false,
                OldPhotoWidth: null,
                OldPhotoHeight: null,
                Holds: []);
        }

        var generation = await db.Walls
            .IgnoreQueryFilters()
            .Where(w => w.Id == wallId)
            .Select(w => w.CurrentGeneration)
            .FirstAsync(ct);

        var holds = await db.Holds
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(h => h.WallId == wallId && h.Generation == generation)
            .Select(h => new
            {
                h.Id,
                h.X,
                h.Y,
                h.Radius,
                h.ShapePoints,
                h.Color,
                h.Category,
                LinkCount = h.BoulderHolds.Count,
            })
            .ToListAsync(ct);

        var inputs = holds
            .Select(h => new StitchHoldInput(
                h.Id,
                h.X,
                h.Y,
                h.Radius,
                h.ShapePoints?.Select(sp => new StitchShapePoint(sp.Dx, sp.Dy)).ToList(),
                h.Color,
                (int)h.Category,
                h.LinkCount))
            .ToList();

        // The sidecar needs the OLD photo's pixel dimensions to de-normalise the hold coordinates;
        // it reads them off the uploaded oldPhoto part, so null here means "take them from the file".
        return new StitchJobOptions(
            options.WallAngleDegrees,
            options.DefaultProjection.ToWire(),
            TransferHolds: true,
            OldPhotoWidth: null,
            OldPhotoHeight: null,
            inputs);
    }

    public static async Task<StitchPhotoUpload?> LoadOldPhotoAsync(BlocwerkDbContext db, Guid wallId, CancellationToken ct)
    {
        var photo = await db.Walls
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(w => w.Id == wallId)
            .Select(w => new { w.Photo, w.PhotoContentType })
            .FirstOrDefaultAsync(ct);

        if (photo?.Photo is null || photo.Photo.Length == 0)
        {
            return null;
        }

        var contentType = photo.PhotoContentType ?? "image/jpeg";
        var extension = contentType.Contains("png", StringComparison.OrdinalIgnoreCase) ? "png" : "jpg";
        return new StitchPhotoUpload($"old-photo.{extension}", contentType, photo.Photo);
    }
}
