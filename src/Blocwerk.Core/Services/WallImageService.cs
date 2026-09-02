using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

/// <inheritdoc cref="IWallImageService"/>
public class WallImageService : IWallImageService
{
    private readonly IDbContextFactory<BlocwerkDbContext> dbContextFactory;
    private readonly IWallImageStorage storage;
    private readonly ILogger<WallImageService> logger;

    public WallImageService(
        IDbContextFactory<BlocwerkDbContext> dbContextFactory,
        IWallImageStorage storage,
        ILogger<WallImageService> logger)
    {
        this.dbContextFactory = dbContextFactory;
        this.storage = storage;
        this.logger = logger;
    }

    public async Task<WallImage> RecordImageAsync(
        Guid wallId,
        string storagePath,
        string contentType,
        long sizeBytes,
        string? caption = null,
        DateTimeOffset? capturedAt = null,
        CancellationToken ct = default)
    {
        // Machine callers hold a wall-scoped key, so there is no user context to filter walls by.
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        db.CurrentUserId = Guid.Empty;

        if (!await db.Walls.IgnoreQueryFilters().AnyAsync(w => w.Id == wallId, ct))
        {
            logger.LogWarning("Wall image rejected: wall {WallId} not found", wallId);
            throw new InvalidOperationException("Wall not found");
        }

        var image = new WallImage
        {
            WallId = wallId,
            StoragePath = storagePath,
            ContentType = contentType,
            SizeBytes = sizeBytes,
            Caption = caption,
            CapturedAt = capturedAt ?? DateTimeOffset.UtcNow,
        };

        db.WallImages.Add(image);
        await db.SaveChangesAsync(ct);
        return image;
    }

    public async Task<IReadOnlyList<WallGalleryItem>> GetGalleryAsync(
        Guid wallId,
        int skip = 0,
        int take = 50,
        CancellationToken ct = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        db.CurrentUserId = Guid.Empty;

        // Three sources, three different stores — merged in memory because no single query spans
        // them. Only metadata is projected, so the legacy byte arrays never leave the database.
        var items = new List<WallGalleryItem>();
        items.AddRange(await LoadUploadsAsync(db, wallId, ct));
        items.AddRange(await LoadWallPhotoAsync(db, wallId, ct));
        items.AddRange(await LoadResetPhotosAsync(db, wallId, ct));

        return items
            .OrderByDescending(i => i.CapturedAt)
            .Skip(Math.Max(0, skip))
            .Take(Math.Max(0, take))
            .ToList();
    }

    public async Task<WallImage?> GetImageAsync(Guid imageId, CancellationToken ct = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        db.CurrentUserId = Guid.Empty;

        return await db.WallImages
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(i => i.Id == imageId, ct);
    }

    public async Task<WallImageContent?> GetLegacyImageContentAsync(
        Guid wallId,
        WallGallerySource source,
        Guid sourceId,
        CancellationToken ct = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        db.CurrentUserId = Guid.Empty;

        if (source == WallGallerySource.WallPhoto)
        {
            var wall = await db.Walls
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(w => w.Id == wallId && w.Id == sourceId && w.Photo != null)
                .Select(w => new { w.Photo, w.PhotoContentType })
                .FirstOrDefaultAsync(ct);

            return wall is null ? null : new WallImageContent(wall.Photo!, wall.PhotoContentType ?? "image/jpeg");
        }

        if (source == WallGallerySource.ResetPhoto)
        {
            var reset = await db.WallResets
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(r => r.Id == sourceId && r.WallId == wallId && r.PreviousPhoto != null)
                .Select(r => new { r.PreviousPhoto, r.PreviousPhotoContentType })
                .FirstOrDefaultAsync(ct);

            return reset is null
                ? null
                : new WallImageContent(reset.PreviousPhoto!, reset.PreviousPhotoContentType ?? "image/jpeg");
        }

        // Uploaded items are files; the web layer streams them through IWallImageStorage.
        return null;
    }

    /// <inheritdoc/>
    public async Task<WallPhotoTag?> GetLegacyImageTagAsync(
        Guid wallId,
        WallGallerySource source,
        Guid sourceId,
        CancellationToken ct = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        db.CurrentUserId = Guid.Empty;

        // Same rows and same filters as GetLegacyImageContentAsync — the caller has already been
        // gated by the endpoint — but projected to a server-side length(bytea) instead of the bytes.
        if (source == WallGallerySource.WallPhoto)
        {
            var wall = await db.Walls
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(w => w.Id == wallId && w.Id == sourceId && w.Photo != null)
                .Select(w => new { Length = w.Photo!.Length, w.PhotoContentType, w.CurrentGeneration })
                .FirstOrDefaultAsync(ct);

            return wall is null
                ? null
                : new WallPhotoTag(wall.Length, wall.PhotoContentType, wall.CurrentGeneration, IsArchived: false);
        }

        if (source == WallGallerySource.ResetPhoto)
        {
            // A reset's archived photo is written once when the reset happens and never rewritten.
            var reset = await db.WallResets
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(r => r.Id == sourceId && r.WallId == wallId && r.PreviousPhoto != null)
                .Select(r => new { Length = r.PreviousPhoto!.Length, r.PreviousPhotoContentType, r.Generation })
                .FirstOrDefaultAsync(ct);

            return reset is null
                ? null
                : new WallPhotoTag(reset.Length, reset.PreviousPhotoContentType, reset.Generation, IsArchived: true);
        }

        return null;
    }

    public async Task DeleteImageAsync(Guid imageId, Guid actingUserId, CancellationToken ct = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        db.CurrentUserId = Guid.Empty;

        var image = await db.WallImages.IgnoreQueryFilters().FirstOrDefaultAsync(i => i.Id == imageId, ct);
        if (image is null)
        {
            throw new InvalidOperationException("Wall image not found");
        }

        await WallAdminGuard.EnsureWallAdminAsync(db, image.WallId, actingUserId, ct);

        var storedName = image.StoragePath;
        db.WallImages.Remove(image);
        await db.SaveChangesAsync(ct);
        storage.Delete(storedName);
        logger.LogInformation("Wall image {ImageId} deleted from wall {WallId} by {UserId}", imageId, image.WallId, actingUserId);
    }

    private static async Task<List<WallGalleryItem>> LoadUploadsAsync(
        BlocwerkDbContext db,
        Guid wallId,
        CancellationToken ct) =>
        await db.WallImages
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(i => i.WallId == wallId)
            .Select(i => new WallGalleryItem(
                i.Id,
                WallGallerySource.Uploaded,
                i.WallId,
                i.ContentType,
                i.SizeBytes,
                i.Caption,
                i.CapturedAt))
            .ToListAsync(ct);

    private static async Task<List<WallGalleryItem>> LoadWallPhotoAsync(
        BlocwerkDbContext db,
        Guid wallId,
        CancellationToken ct) =>
        await db.Walls
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(w => w.Id == wallId && w.Photo != null)
            .Select(w => new WallGalleryItem(
                w.Id,
                WallGallerySource.WallPhoto,
                w.Id,
                w.PhotoContentType ?? "image/jpeg",
                w.Photo!.Length,
                "Current wall photo",
                w.LastResetAt ?? w.CreatedAt))
            .ToListAsync(ct);

    private static async Task<List<WallGalleryItem>> LoadResetPhotosAsync(
        BlocwerkDbContext db,
        Guid wallId,
        CancellationToken ct) =>
        await db.WallResets
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(r => r.WallId == wallId && r.PreviousPhoto != null)
            .Select(r => new WallGalleryItem(
                r.Id,
                WallGallerySource.ResetPhoto,
                r.WallId,
                r.PreviousPhotoContentType ?? "image/jpeg",
                r.PreviousPhoto!.Length,
                "Generation " + r.Generation,
                r.ResetAt))
            .ToListAsync(ct);
}
