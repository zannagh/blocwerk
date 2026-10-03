// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Capture.Retention;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Capture;

/// <summary>
/// Retention for capture data — wall photos can show people, so nothing is kept without a reason:
/// <list type="number">
/// <item>drafts nobody submitted, <see cref="WallCapturePipelineOptions.DraftLifetime"/> after their last upload;</item>
/// <item>photos of a finished or failed capture, <see cref="WallCapturePipelineOptions.PhotoRetention"/>
/// after it ended — unless that capture produced the wall's ACTIVE model;</item>
/// <item>stored images no photo or texture row references (a deleted wall or model cascades its rows
/// in the database, not its files), once older than <see cref="WallCapturePipelineOptions.OrphanGrace"/>.</item>
/// <item>a capture's sparse points (<see cref="WallCapture.SparsePointsStoredPath"/>) follow its photos.</item>
/// <item>retired models' textures and photo-real views beyond the few kept for a revert (<see cref="SupersededModelRetention"/>);</item>
/// <item>3D runners' trained results once the view is long installed (<see cref="Runners.GpuJobQueue.DropAgedResultsAsync"/>);</item>
/// <item>abandoned capture imports (<see cref="ImportStagingRetention"/>).</item>
/// </list>
/// The last three only log what they would free while <see cref="WallCapturePipelineOptions.RetentionDryRun"/> is set.
/// A capture's walk-along video and its extracted frames (<see cref="CaptureVideoFiles"/>) follow its
/// photos: removed with a draft, and with the photos once they expire.
/// Only the store's known kinds (images, videos, texture source maps, runner files, sparse clouds, photo-real
/// scenes) are treated as orphans: a later stage that keeps other kinds of file in the capture store owns their
/// cleanup (and one that keeps a known kind there must add its rows to <see cref="ReferencedAsync"/>).
/// </summary>
public sealed class WallCaptureSweeper(
    RootDbContextFactory dbContextFactory,
    ICaptureFileStore files,
    WallCapturePipelineOptions options,
    ILogger<WallCaptureSweeper> logger)
{
    // .json: the textures' source-view maps (nothing else in the capture store is JSON); .spz: photo-real scenes
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".mp4", ".mov", ".m4v", ".json", ".zip", ".prep", ".upl", ".spz", Geometry.Sparse.SparseCloudFile.Extension };

    private long freedBytes;

    public async Task<CaptureSweepResult> SweepAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        Interlocked.Exchange(ref freedBytes, 0);
        var drafts = await SweepDraftsAsync(now, ct);
        var expired = await SweepExpiredPhotosAsync(now, ct);
        var models = await SupersededModelRetention.RunAsync(dbContextFactory, files, options, now, logger, ct);
        var results = await Runners.GpuJobQueue.DropAgedResultsAsync(dbContextFactory, files, options, now, logger, ct);
        var imports = await ImportStagingRetention.RunAsync(files, options, now, logger, ct);
        var orphans = await SweepOrphansAsync(now, ct);
        var result = new CaptureSweepResult(drafts, expired, orphans)
        {
            SupersededModels = models,
            RunnerResults = results,
            AbandonedImports = imports,
            FreedBytes = Interlocked.Read(ref freedBytes) + (options.RetentionDryRun ? 0 : models.Bytes + results.Bytes + imports.Bytes),
        };
        Log(result);
        return result;
    }

    /// <summary>
    /// Removes drafts idle for <see cref="WallCapturePipelineOptions.DraftLifetime"/>: measured from their last photo or video
    /// upload (or creation), never while an open "Update panels + 3D" run still owns them (that run expires on its own).
    /// </summary>
    /// <param name="now">The sweep time.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>How many drafts were removed.</returns>
    public async Task<int> SweepDraftsAsync(DateTimeOffset now, CancellationToken ct)
    {
        await using var db = dbContextFactory.CreateDbContext();
        var cutoff = now - options.DraftLifetime;
        var owned = await db.WallRefreshes
            .Where(r => r.CaptureId != null && r.Status != WallRefreshStatus.Done && r.Status != WallRefreshStatus.Failed
                        && r.Status != WallRefreshStatus.Discarded)
            .Select(r => r.CaptureId!.Value)
            .ToListAsync(ct);

        // Filtered in memory: SQLite cannot compare DateTimeOffset in SQL.
        var candidates = (await db.WallCaptures.Where(c => c.Status == WallCaptureStatus.Draft && !owned.Contains(c.Id)).ToListAsync(ct))
            .Where(c => c.CreatedAt < cutoff)
            .ToList();
        var candidateIds = candidates.Select(d => d.Id).ToList();
        var uploads = (await db.WallCapturePhotos.Where(p => candidateIds.Contains(p.CaptureId))
                .Select(p => new { p.CaptureId, p.UploadedAt })
                .ToListAsync(ct))
            .GroupBy(p => p.CaptureId)
            .ToDictionary(g => g.Key, g => g.Max(p => p.UploadedAt));
        var drafts = candidates
            .Where(c => (!uploads.TryGetValue(c.Id, out var last) || last < cutoff) && !(VideoUploadedAt(c.VideoStoredPath) >= cutoff))
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

    private void Log(CaptureSweepResult r)
    {
        var retained = r.SupersededModels + r.RunnerResults + r.AbandonedImports;
        var dry = options.RetentionDryRun;
        if (dry && retained.Count > 0)
        {
            logger.LogInformation(
                "Capture retention (dry run) would free {Bytes}: the 3D files of {Models} retired model(s) ({ModelBytes}), "
                + "{Results} runner result(s) ({ResultBytes}), {Imports} abandoned import(s) ({ImportBytes})",
                RetentionFiles.Format(retained.Bytes), r.SupersededModels.Count, RetentionFiles.Format(r.SupersededModels.Bytes),
                r.RunnerResults.Count, RetentionFiles.Format(r.RunnerResults.Bytes), r.AbandonedImports.Count,
                RetentionFiles.Format(r.AbandonedImports.Bytes));
        }

        if (r.Drafts + r.ExpiredPhotos + r.OrphanFiles + (dry ? 0 : retained.Count) > 0)
        {
            logger.LogInformation(
                "Capture sweep freed {Bytes}: {Drafts} draft(s), {Photos} expired photo(s), {Orphans} orphan file(s), "
                + "the 3D files of {Models} retired model(s), {Results} runner result(s), {Imports} abandoned import(s)",
                RetentionFiles.Format(r.FreedBytes), r.Drafts, r.ExpiredPhotos, r.OrphanFiles,
                dry ? 0 : r.SupersededModels.Count, dry ? 0 : r.RunnerResults.Count, dry ? 0 : r.AbandonedImports.Count);
        }
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
                        || c.Status == WallCaptureStatus.SucceededWithoutSplat || c.Status == WallCaptureStatus.Failed
                        || c.Status == WallCaptureStatus.StoredNotActivated)
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
            .Where(c => expiredIds.Contains(c.Id) && (c.VideoStoredPath != null || c.VideoFramesJson != null || c.SparsePointsStoredPath != null))
            .ToListAsync(ct);
        var videoFiles = withVideo.SelectMany(CaptureVideoFiles.Of).ToList();
        var sparseFiles = withVideo.Select(c => c.SparsePointsStoredPath).OfType<string>().ToList();
        foreach (var capture in withVideo)
        {
            capture.VideoStoredPath = null;
            capture.VideoFramesJson = null;
            capture.SparsePointsStoredPath = null;
        }

        // A photo-real view still waiting for (or on) a 3D runner goes with the photos: its bundle is a copy of them.
        var gpuJobs = await Runners.GpuJobQueue.CancelActiveAsync(
            db, expiredIds, "the capture's photos were deleted (photo retention)", now, ct);
        var gpuFiles = gpuJobs.SelectMany(Runners.GpuJobQueue.FilesOf)
            .Concat(await Runners.GpuJobQueue.DropLeftoversAsync(db, expiredIds, null, ct)).ToList();
        await db.SaveChangesAsync(ct);
        DeleteFiles(photos.Select(p => p.StoredPath).Concat(videoFiles).Concat(sparseFiles).Concat(gpuFiles));
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
        var sparse = await db.WallCaptures.Where(c => c.SparsePointsStoredPath != null).Select(c => c.SparsePointsStoredPath!).ToListAsync(ct);
        var splats = (await db.WallGeometrySplats.AsNoTracking().ToListAsync(ct)).SelectMany(SplatLodLadder.Files).OfType<string>();

        // .zip/.prep/.upl: the 3D runners' bundles, prepared state, uploaded results and previews of jobs still in play, and
        // every job's leftover (its trained result or installed preview, kept to finish again). Anything else of a finished,
        // failed or cancelled job is deleted with it; what survived a failed delete is an orphan here.
        var gpu = await Runners.GpuJobQueue.ReferencedFilesAsync(db, ct);

        // Videos dropped into an "Update panels + 3D" run, kept until its 3D capture starts.
        var refreshVideos = await Refresh.RefreshTimeline.StoredVideosAsync(db, ct);
        return new HashSet<string>(
            photos.Concat(textures).Concat(masks).Concat(sourceMaps).Concat(videos).Concat(sparse).Concat(splats).Concat(gpu).Concat(refreshVideos),
            StringComparer.Ordinal);
    }

    /// <summary>When a draft's walk-along video was stored (its file's write time); null without one.</summary>
    private DateTimeOffset? VideoUploadedAt(string? storedPath)
    {
        var path = storedPath is null ? null : files.ResolvePhysicalPath(storedPath);
        return path is not null && File.Exists(path) ? new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero) : null;
    }

    private void DeleteFiles(IEnumerable<string> names) =>
        Interlocked.Add(ref freedBytes, RetentionFiles.Delete(files, names, logger));
}
