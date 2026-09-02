using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Blocwerk.Web.Endpoints;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Web.Maintenance;

/// <summary>
/// Enumerates every image the variant pipeline can serve, as cache keys plus a way to fetch the
/// original. Metadata only: each query projects to <c>length(bytea)</c> and the small columns the
/// tag is built from, so building the whole list never moves a blob and the connection is handed
/// back before any rendering starts.
/// </summary>
/// <remarks>
/// The keys come from <c>ImageResponse</c> and <see cref="ImageIdentity"/> — the same derivation
/// the byte routes use — and the tags are rebuilt from the same columns the tag readers project.
/// Query filters are ignored throughout: this runs with no signed-in user, and warming a cache
/// discloses nothing (the routes still gate every byte they serve).
/// </remarks>
public static class ImageWarmTargets
{
    public static async Task<List<ImageWarmTarget>> CollectAsync(
        IDbContextFactory<BlocwerkDbContext> factory,
        IWallImageStorage storage,
        CancellationToken ct)
    {
        var targets = new List<ImageWarmTarget>();

        await using (var db = await factory.CreateDbContextAsync(ct))
        {
            await AddWallPhotosAsync(db, factory, targets, ct);
            await AddResetPhotosAsync(db, factory, targets, ct);
            await AddPanelPhotosAsync(db, factory, targets, ct);
            await AddUploadedImagesAsync(db, storage, targets, ct);
        }

        return targets;
    }

    /// <summary>
    /// The live photo, the staged photo, and the live photo again under the two OTHER routes that
    /// address it — <c>/photo/{currentGeneration}</c> and the gallery's <c>wallphoto</c> item. Each
    /// has its own identity, so each needs its own cache entry.
    /// </summary>
    private static async Task AddWallPhotosAsync(
        BlocwerkDbContext db,
        IDbContextFactory<BlocwerkDbContext> factory,
        List<ImageWarmTarget> targets,
        CancellationToken ct)
    {
        var walls = await db.Walls
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Select(w => new
            {
                w.Id,
                w.Name,
                w.CurrentGeneration,
                PhotoLength = w.Photo == null ? 0 : w.Photo.Length,
                w.PhotoContentType,
                StagedLength = w.StagedPhoto == null ? 0 : w.StagedPhoto.Length,
                w.StagedPhotoContentType,
                w.StagedAt,
            })
            .ToListAsync(ct);

        foreach (var wall in walls)
        {
            if (wall.PhotoLength > 0)
            {
                var tag = new WallPhotoTag(
                    wall.PhotoLength, wall.PhotoContentType, wall.CurrentGeneration, IsArchived: false);
                var id = wall.Id;
                Func<CancellationToken, Task<byte[]?>> load = token => LoadWallPhotoAsync(factory, id, staged: false, token);

                targets.Add(new ImageWarmTarget(
                    ImageResponse.VariantKey(tag, ImageIdentity.WallPhoto(id)),
                    $"wall '{wall.Name}' photo", load));
                targets.Add(new ImageWarmTarget(
                    ImageResponse.VariantKey(tag, ImageIdentity.WallGenerationPhoto(id, wall.CurrentGeneration)),
                    $"wall '{wall.Name}' photo @gen {wall.CurrentGeneration}", load));
                targets.Add(new ImageWarmTarget(
                    ImageResponse.VariantKey(
                        tag, ImageIdentity.LegacyGalleryImage(id, WallGallerySource.WallPhoto, id)),
                    $"wall '{wall.Name}' gallery photo", load));
            }

            if (wall.StagedLength > 0)
            {
                var tag = new WallPhotoTag(
                    wall.StagedLength, wall.StagedPhotoContentType, wall.StagedAt?.UtcTicks ?? 0L, IsArchived: false);
                var id = wall.Id;

                targets.Add(new ImageWarmTarget(
                    ImageResponse.VariantKey(tag, ImageIdentity.StagedWallPhoto(id)),
                    $"wall '{wall.Name}' staged photo",
                    token => LoadWallPhotoAsync(factory, id, staged: true, token)));
            }
        }
    }

    /// <summary>
    /// A retired generation's archived photo, addressable both by generation number and as a
    /// gallery item. Both are immutable and both are linked from the UI.
    /// </summary>
    private static async Task AddResetPhotosAsync(
        BlocwerkDbContext db,
        IDbContextFactory<BlocwerkDbContext> factory,
        List<ImageWarmTarget> targets,
        CancellationToken ct)
    {
        var resets = await db.WallResets
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(r => r.PreviousPhoto != null)
            .Select(r => new
            {
                r.Id,
                r.WallId,
                r.Generation,
                Length = r.PreviousPhoto!.Length,
                r.PreviousPhotoContentType,
            })
            .ToListAsync(ct);

        foreach (var reset in resets)
        {
            var tag = new WallPhotoTag(
                reset.Length, reset.PreviousPhotoContentType, reset.Generation, IsArchived: true);
            var resetId = reset.Id;
            Func<CancellationToken, Task<byte[]?>> load = token => LoadResetPhotoAsync(factory, resetId, token);

            targets.Add(new ImageWarmTarget(
                ImageResponse.VariantKey(tag, ImageIdentity.WallGenerationPhoto(reset.WallId, reset.Generation)),
                $"wall {reset.WallId} photo @gen {reset.Generation}", load));
            targets.Add(new ImageWarmTarget(
                ImageResponse.VariantKey(
                    tag, ImageIdentity.LegacyGalleryImage(reset.WallId, WallGallerySource.ResetPhoto, resetId)),
                $"wall {reset.WallId} gallery reset photo {resetId}", load));
        }
    }

    /// <summary>Per-panel photos of a big wall, live and staged.</summary>
    private static async Task AddPanelPhotosAsync(
        BlocwerkDbContext db,
        IDbContextFactory<BlocwerkDbContext> factory,
        List<ImageWarmTarget> targets,
        CancellationToken ct)
    {
        var panels = await db.WallPanels
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Select(p => new
            {
                p.Id,
                p.Generation,
                PhotoLength = p.Photo == null ? 0 : p.Photo.Length,
                p.PhotoContentType,
                StagedLength = p.StagedPhoto == null ? 0 : p.StagedPhoto.Length,
                p.StagedPhotoContentType,
                p.StagedAt,
            })
            .ToListAsync(ct);

        foreach (var panel in panels)
        {
            var id = panel.Id;

            if (panel.PhotoLength > 0)
            {
                var tag = new WallPhotoTag(
                    panel.PhotoLength, panel.PhotoContentType, panel.Generation, IsArchived: false);

                targets.Add(new ImageWarmTarget(
                    ImageResponse.VariantKey(tag, ImageIdentity.PanelPhoto(id, ImageIdentity.LiveSlot)),
                    $"panel {id} photo",
                    token => LoadPanelPhotoAsync(factory, id, staged: false, token)));
            }

            if (panel.StagedLength > 0)
            {
                var tag = new WallPhotoTag(
                    panel.StagedLength, panel.StagedPhotoContentType, panel.StagedAt?.UtcTicks ?? 0L, IsArchived: false);

                targets.Add(new ImageWarmTarget(
                    ImageResponse.VariantKey(tag, ImageIdentity.PanelPhoto(id, ImageIdentity.StagedSlot)),
                    $"panel {id} staged photo",
                    token => LoadPanelPhotoAsync(factory, id, staged: true, token)));
            }
        }
    }

    /// <summary>Gallery uploads, whose bytes are files rather than blobs.</summary>
    private static async Task AddUploadedImagesAsync(
        BlocwerkDbContext db,
        IWallImageStorage storage,
        List<ImageWarmTarget> targets,
        CancellationToken ct)
    {
        var images = await db.WallImages
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Select(i => new { i.Id, i.StoragePath, i.ContentType, i.SizeBytes, i.CapturedAt })
            .ToListAsync(ct);

        foreach (var image in images)
        {
            var path = storage.ResolvePhysicalPath(image.StoragePath);
            if (path is null)
            {
                continue;
            }

            targets.Add(new ImageWarmTarget(
                ImageResponse.UploadedGalleryKey(image.Id, image.SizeBytes, image.CapturedAt, image.ContentType),
                $"uploaded image {image.Id}",
                token => LoadFileAsync(path, token)));
        }
    }

    private static async Task<byte[]?> LoadFileAsync(string path, CancellationToken ct) =>
        File.Exists(path) ? await File.ReadAllBytesAsync(path, ct) : null;

    private static async Task<byte[]?> LoadWallPhotoAsync(
        IDbContextFactory<BlocwerkDbContext> factory, Guid wallId, bool staged, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Walls
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(w => w.Id == wallId)
            .Select(w => staged ? w.StagedPhoto : w.Photo)
            .FirstOrDefaultAsync(ct);
    }

    private static async Task<byte[]?> LoadResetPhotoAsync(
        IDbContextFactory<BlocwerkDbContext> factory, Guid resetId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.WallResets
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(r => r.Id == resetId)
            .Select(r => r.PreviousPhoto)
            .FirstOrDefaultAsync(ct);
    }

    private static async Task<byte[]?> LoadPanelPhotoAsync(
        IDbContextFactory<BlocwerkDbContext> factory, Guid panelId, bool staged, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.WallPanels
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(p => p.Id == panelId)
            .Select(p => staged ? p.StagedPhoto : p.Photo)
            .FirstOrDefaultAsync(ct);
    }
}
