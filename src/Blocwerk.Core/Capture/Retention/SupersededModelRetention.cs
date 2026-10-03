// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Capture.Corrections;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Capture.Retention;

/// <summary>
/// Retired models' wall textures and photo-real views (full scene, level-of-detail ladder, mobile and uncleaned copies:
/// 0.2–0.9 GB a capture) would otherwise stay forever. Per wall, the families (<see cref="ModelFamily"/>: a capture's
/// model and its corrections) other than the active model's are ranked by when they were last live; the newest
/// <see cref="WallCapturePipelineOptions.KeepSupersededModels"/> keep their files (activating one brings its 3D view back),
/// and so does anything retired less than <see cref="WallCapturePipelineOptions.SupersededModelGrace"/> ago. The others
/// lose their texture and splat rows and files and are marked <see cref="WallGeometryModel.FilesRemovedAt"/>; the model
/// row (its geometry, the history, everything keyed on it) stays.
/// </summary>
/// <remarks>
/// A model of a capture still awaiting an admin's decision (<see cref="WallCaptureStatus.StoredNotActivated"/>) never
/// counts and keeps its files as long as its capture's photos. A wall with work in flight (a capture, a re-solve or
/// re-render, a runner job, an open "Update panels + 3D" run) is skipped until the next sweep. A file another row still
/// references (a correction shares its parent's) is never deleted.
/// </remarks>
public static class SupersededModelRetention
{
    /// <summary>Applies the rule (or, in a dry run, measures it).</summary>
    public static async Task<RetentionOutcome> RunAsync(
        RootDbContextFactory dbContextFactory, ICaptureFileStore files, WallCapturePipelineOptions options, DateTimeOffset now,
        ILogger logger, CancellationToken ct)
    {
        if (options.KeepSupersededModels is not { } keep)
        {
            return RetentionOutcome.None;
        }

        await using var db = dbContextFactory.CreateDbContext();
        var doomed = await SelectAsync(db, keep, options, now, ct);
        if (doomed.Count == 0)
        {
            return RetentionOutcome.None;
        }

        if (options.RetentionDryRun)
        {
            var (textures, splats) = await RowsAsync(db, doomed, ct);
            var kept = await ReferencedOutsideAsync(db, doomed, ct);
            return new(doomed.Count, RetentionFiles.SizeOf(files, FilesOf(textures, splats).Where(f => !kept.Contains(f))));
        }

        var removed = new List<string>();
        var count = 0;
        foreach (var modelId in doomed)
        {
            if (await StripAsync(db, modelId, now, removed, ct))
            {
                count++;
            }
        }

        var shared = await SharedCaptureFiles.ReferencedAsync(db, ct);
        return new(count, RetentionFiles.Delete(files, removed.Where(f => !shared.Contains(f)), logger));
    }

    /// <summary>The models whose files go, by the rule above.</summary>
    internal static async Task<List<Guid>> SelectAsync(
        BlocwerkDbContext db, int keep, WallCapturePipelineOptions options, DateTimeOffset now, CancellationToken ct)
    {
        var models = await db.WallGeometryModels.AsNoTracking()
            .Select(m => new RetainedModel(m.Id, m.WallId, m.IsActive, m.DerivedFromModelId, m.RetiredAt ?? m.CreatedAt))
            .ToListAsync(ct);
        var withFiles = (await db.WallGeometryTextures.Select(t => t.GeometryModelId).Distinct().ToListAsync(ct))
            .Concat(await db.WallGeometrySplats.Select(s => s.GeometryModelId).ToListAsync(ct))
            .ToHashSet();
        var pending = (await db.WallCaptures
                .Where(c => c.Status == WallCaptureStatus.StoredNotActivated && c.GeometryModelId != null)
                .Select(c => c.GeometryModelId!.Value)
                .ToListAsync(ct))
            .ToHashSet();
        var busy = await RetentionBusyWalls.ListAsync(db, ct);

        // A pending model keeps its files while its capture keeps its photos (the admin may still activate it).
        var pendingGrace = options.PhotoRetention is { } photos ? Max(photos, options.SupersededModelGrace) : (TimeSpan?)null;
        var doomed = new List<Guid>();
        foreach (var wall in models.GroupBy(m => m.WallId).Where(w => !busy.Contains(w.Key)))
        {
            if (wall.FirstOrDefault(m => m.IsActive) is not { } active)
            {
                continue;
            }

            var parents = wall.ToDictionary(m => m.Id, m => m.DerivedFromModelId);
            var families = wall.GroupBy(m => ModelFamily.Root(parents, m.Id))
                .Where(f => f.Key != ModelFamily.Root(parents, active.Id))
                .Select(f => (Members: f.ToList(), Pending: f.Any(m => pending.Contains(m.Id)), Since: f.Max(m => m.Since)))
                .OrderByDescending(f => f.Since)
                .ToList();
            var superseded = families.Where(f => !f.Pending).Skip(keep)
                .Where(f => now - f.Since >= options.SupersededModelGrace);
            var stale = families.Where(f => f.Pending && pendingGrace is { } grace && now - f.Since >= grace);
            doomed.AddRange(superseded.Concat(stale).SelectMany(f => f.Members).Where(m => withFiles.Contains(m.Id)).Select(m => m.Id));
        }

        return doomed;
    }

    /// <summary>
    /// Marks the model (only while it is still inactive: an admin may have activated it since) and drops its texture and
    /// splat rows in one transaction; their files are added to <paramref name="removed"/>. False when it was activated.
    /// </summary>
    private static async Task<bool> StripAsync(BlocwerkDbContext db, Guid modelId, DateTimeOffset now, List<string> removed, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var marked = await db.WallGeometryModels.Where(m => m.Id == modelId && !m.IsActive)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.FilesRemovedAt, now), ct);
        if (marked == 0)
        {
            return false;
        }

        var (textures, splats) = await RowsAsync(db, [modelId], ct);
        db.WallGeometryTextures.RemoveRange(textures);
        db.WallGeometrySplats.RemoveRange(splats);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        removed.AddRange(FilesOf(textures, splats));
        return true;
    }

    private static async Task<(List<WallGeometryTexture> Textures, List<WallGeometrySplat> Splats)> RowsAsync(
        BlocwerkDbContext db, IReadOnlyCollection<Guid> modelIds, CancellationToken ct) =>
        (await db.WallGeometryTextures.Where(t => modelIds.Contains(t.GeometryModelId)).ToListAsync(ct),
            await db.WallGeometrySplats.Where(s => modelIds.Contains(s.GeometryModelId)).ToListAsync(ct));

    /// <summary>The files rows of models other than <paramref name="modelIds"/> reference (what a dry run would keep).</summary>
    private static async Task<HashSet<string>> ReferencedOutsideAsync(BlocwerkDbContext db, IReadOnlyCollection<Guid> modelIds, CancellationToken ct)
    {
        var textures = await db.WallGeometryTextures.AsNoTracking().Where(t => !modelIds.Contains(t.GeometryModelId)).ToListAsync(ct);
        var splats = await db.WallGeometrySplats.AsNoTracking().Where(s => !modelIds.Contains(s.GeometryModelId)).ToListAsync(ct);
        return FilesOf(textures, splats).ToHashSet(StringComparer.Ordinal);
    }

    private static IEnumerable<string> FilesOf(IEnumerable<WallGeometryTexture> textures, IEnumerable<WallGeometrySplat> splats) =>
        textures.SelectMany(t => new[] { t.StoredPath, t.MaskStoredPath, t.SourceMapStoredPath })
            .Concat(splats.SelectMany(SplatLodLadder.Files))
            .OfType<string>();

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
}
