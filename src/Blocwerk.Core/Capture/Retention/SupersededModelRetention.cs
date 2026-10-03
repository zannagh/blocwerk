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
/// 0.2–0.9 GB a capture) would otherwise stay forever. <see cref="SupersededModelSelection"/> picks the models whose files
/// go: everything but the active model's family, the newest <see cref="WallCapturePipelineOptions.KeepSupersededModels"/>
/// families that were active once (activating one brings its 3D view back), the models those carry textures from, and
/// anything retired less than <see cref="WallCapturePipelineOptions.SupersededModelGrace"/> ago. Those lose their texture
/// and splat rows and files and are marked <see cref="WallGeometryModel.FilesRemovedAt"/>; the model row (its geometry,
/// the history, everything keyed on it) stays.
/// </summary>
/// <remarks>
/// Each wall is stripped in one transaction that selects again first: a model that became active, joined the active
/// family or a kept one, or a wall that got busy meanwhile is skipped. A file another row still references (a correction
/// shares its parent's) is never deleted.
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
        var doomed = await SupersededModelSelection.SelectAsync(db, keep, options, now, ct);
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
        var walls = await db.WallGeometryModels.AsNoTracking().Where(m => doomed.Contains(m.Id))
            .Select(m => new { m.Id, m.WallId }).ToListAsync(ct);
        foreach (var wall in walls.GroupBy(m => m.WallId))
        {
            count += await StripWallAsync(db, wall.Key, wall.Select(m => m.Id).ToList(), keep, options, now, removed, ct);
        }

        var shared = await SharedCaptureFiles.ReferencedAsync(db, ct);
        return new(count, RetentionFiles.Delete(files, removed.Where(f => !shared.Contains(f)), logger));
    }

    /// <summary>
    /// Strips those of <paramref name="candidates"/> (chosen earlier) that a fresh selection inside the transaction still
    /// picks, marking each only while it is inactive. Their files are added to <paramref name="removed"/>, for deleting
    /// after the commit. Returns how many models were stripped.
    /// </summary>
    internal static async Task<int> StripWallAsync(
        BlocwerkDbContext db, Guid wallId, IReadOnlyCollection<Guid> candidates, int keep, WallCapturePipelineOptions options,
        DateTimeOffset now, List<string> removed, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var still = (await SupersededModelSelection.SelectAsync(db, keep, options, now, ct, wallId)).Intersect(candidates).ToList();
        var stripped = new List<Guid>();
        foreach (var modelId in still)
        {
            var marked = await db.WallGeometryModels.Where(m => m.Id == modelId && !m.IsActive)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.FilesRemovedAt, now), ct);
            if (marked > 0)
            {
                stripped.Add(modelId);
            }
        }

        var (textures, splats) = await RowsAsync(db, stripped, ct);
        db.WallGeometryTextures.RemoveRange(textures);
        db.WallGeometrySplats.RemoveRange(splats);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        removed.AddRange(FilesOf(textures, splats));
        return stripped.Count;
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
}
