using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Capture;

/// <summary>What one sweep removed.</summary>
public sealed record CaptureSweepResult(int Drafts, int ExpiredPhotos, int OrphanFiles);

/// <summary>
/// Retention for capture data — wall photos can show people, so nothing is kept without a reason:
/// <list type="number">
/// <item>drafts nobody submitted, after <see cref="WallCapturePipelineOptions.DraftLifetime"/>;</item>
/// <item>photos of a finished or failed capture, <see cref="WallCapturePipelineOptions.PhotoRetention"/>
/// after it ended — unless that capture produced the wall's ACTIVE model;</item>
/// <item>stored images no photo or texture row references (a deleted wall or model cascades its rows
/// in the database, not its files), once older than <see cref="WallCapturePipelineOptions.OrphanGrace"/>.</item>
/// </list>
/// A capture's walk-along video and its extracted frames (<see cref="CaptureVideoFiles"/>) follow its
/// photos: removed with a draft, and with the photos once they expire.
/// Only images and capture videos are treated as orphans: a later stage that keeps other
/// kinds of file in the capture store owns their cleanup (and one that keeps images there must add its
/// rows to <see cref="ReferencedAsync"/>).
/// </summary>
public sealed class WallCaptureSweeper(
    RootDbContextFactory dbContextFactory,
    ICaptureFileStore files,
    WallCapturePipelineOptions options,
    ILogger<WallCaptureSweeper> logger)
{
    // .json: the textures' source-view maps (nothing else in the capture store is JSON)
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".mp4", ".mov", ".m4v", ".json" };

    public async Task<CaptureSweepResult> SweepAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var drafts = await SweepDraftsAsync(now, ct);
        var expired = await SweepExpiredPhotosAsync(now, ct);
        var orphans = await SweepOrphansAsync(now, ct);
        if (drafts + expired + orphans > 0)
        {
            logger.LogInformation(
                "Capture sweep removed {Drafts} draft(s), {Photos} expired photo(s), {Orphans} orphan file(s)",
                drafts, expired, orphans);
        }

        return new CaptureSweepResult(drafts, expired, orphans);
    }

    private async Task<int> SweepDraftsAsync(DateTimeOffset now, CancellationToken ct)
    {
        await using var db = dbContextFactory.CreateDbContext();
        var cutoff = now - options.DraftLifetime;

        // Filtered in memory: SQLite cannot compare DateTimeOffset in SQL.
        var drafts = (await db.WallCaptures.Where(c => c.Status == WallCaptureStatus.Draft).ToListAsync(ct))
            .Where(c => c.CreatedAt < cutoff)
            .ToList();
        if (drafts.Count == 0)
        {
            return 0;
        }

        var ids = drafts.Select(d => d.Id).ToList();
        var paths = await db.WallCapturePhotos.Where(p => ids.Contains(p.CaptureId)).Select(p => p.StoredPath).ToListAsync(ct);
        paths.AddRange(drafts.SelectMany(CaptureVideoFiles.Of));
        db.WallCaptures.RemoveRange(drafts);
        await db.SaveChangesAsync(ct);
        DeleteFiles(paths);
        return drafts.Count;
    }

    private async Task<int> SweepExpiredPhotosAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (options.PhotoRetention is not { } retention)
        {
            return 0;
        }

        await using var db = dbContextFactory.CreateDbContext();
        var activeModels = await db.WallGeometryModels.Where(m => m.IsActive).Select(m => m.Id).ToListAsync(ct);
        var ended = await db.WallCaptures
            .Where(c => c.Status == WallCaptureStatus.Succeeded || c.Status == WallCaptureStatus.SucceededWithoutTextures
                        || c.Status == WallCaptureStatus.SucceededWithoutSplat || c.Status == WallCaptureStatus.Failed)
            .Select(c => new { c.Id, c.GeometryModelId, c.CompletedAt, c.CreatedAt })
            .ToListAsync(ct);
        var expiredIds = ended
            .Where(c => (c.CompletedAt ?? c.CreatedAt) < now - retention
                        && !(c.GeometryModelId is { } model && activeModels.Contains(model)))
            .Select(c => c.Id)
            .ToList();
        if (expiredIds.Count == 0)
        {
            return 0;
        }

        var photos = await db.WallCapturePhotos.Where(p => expiredIds.Contains(p.CaptureId)).ToListAsync(ct);
        db.WallCapturePhotos.RemoveRange(photos);
        var withVideo = await db.WallCaptures
            .Where(c => expiredIds.Contains(c.Id) && (c.VideoStoredPath != null || c.VideoFramesJson != null))
            .ToListAsync(ct);
        var videoFiles = withVideo.SelectMany(CaptureVideoFiles.Of).ToList();
        foreach (var capture in withVideo)
        {
            capture.VideoStoredPath = null;
            capture.VideoFramesJson = null;
        }

        await db.SaveChangesAsync(ct);
        DeleteFiles(photos.Select(p => p.StoredPath).Concat(videoFiles));
        return photos.Count;
    }

    private async Task<int> SweepOrphansAsync(DateTimeOffset now, CancellationToken ct)
    {
        var cutoff = now - options.OrphanGrace;
        var candidates = files.ListFiles()
            .Where(f => f.WrittenAt < cutoff && ImageExtensions.Contains(Path.GetExtension(f.Name)))
            .ToList();
        var removed = files.PurgeTemp(options.OrphanGrace);
        if (candidates.Count == 0)
        {
            return removed;
        }

        var referenced = await ReferencedAsync(ct);
        var orphans = candidates.Where(f => !referenced.Contains(f.Name)).Select(f => f.Name).ToList();
        DeleteFiles(orphans);
        return removed + orphans.Count;
    }

    /// <summary>Every stored name a row still points at.</summary>
    private async Task<HashSet<string>> ReferencedAsync(CancellationToken ct)
    {
        await using var db = dbContextFactory.CreateDbContext();
        var photos = await db.WallCapturePhotos.Select(p => p.StoredPath).ToListAsync(ct);
        var textures = await db.WallGeometryTextures.Select(t => t.StoredPath).ToListAsync(ct);
        var masks = await db.WallGeometryTextures.Where(t => t.MaskStoredPath != null)
            .Select(t => t.MaskStoredPath!).ToListAsync(ct);
        var sourceMaps = await db.WallGeometryTextures.Where(t => t.SourceMapStoredPath != null)
            .Select(t => t.SourceMapStoredPath!).ToListAsync(ct);
        var videos = (await db.WallCaptures
                .Where(c => c.VideoStoredPath != null || c.VideoFramesJson != null)
                .Select(c => new { c.VideoStoredPath, c.VideoFramesJson })
                .ToListAsync(ct))
            .SelectMany(c => CaptureVideoFiles.Of(c.VideoStoredPath, c.VideoFramesJson));
        return new HashSet<string>(photos.Concat(textures).Concat(masks).Concat(sourceMaps).Concat(videos), StringComparer.Ordinal);
    }

    private void DeleteFiles(IEnumerable<string> names)
    {
        foreach (var name in names)
        {
            try
            {
                files.Delete(name);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Could not delete capture file {File}; the next sweep retries", name);
            }
        }
    }
}
