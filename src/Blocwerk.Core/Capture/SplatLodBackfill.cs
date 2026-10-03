// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Capture;

/// <summary>
/// Builds the level-of-detail ladder (<see cref="SplatLodLadder"/>) of every photo-real scene stored
/// before it existed, once, off the startup path. Until a row has its ladder the 3D view falls back to
/// the legacy mobile copy and the full scene, as before. Rows are only ever extended (new files and
/// the ladder JSON), never rewritten, so a failure leaves the scene exactly as it was.
/// </summary>
public sealed class SplatLodBackfill(
    RootDbContextFactory dbContextFactory,
    ICaptureFileStore files,
    ILogger<SplatLodBackfill> logger) : BackgroundService
{
    /// <summary>Builds the missing ladders. Returns how many rows got one.</summary>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        List<Guid> ids;
        await using (var db = dbContextFactory.CreateDbContext())
        {
            ids = await db.WallGeometrySplats.AsNoTracking()
                .Where(s => s.LodLevelsJson == null)
                .Select(s => s.Id)
                .ToListAsync(ct);
        }

        var built = 0;
        foreach (var id in ids)
        {
            if (await BuildOneAsync(id, ct))
            {
                built++;
            }
        }

        return built;
    }

    /// <summary>
    /// Saves the pruned levels of <paramref name="spz"/> and returns them with the full scene's splat
    /// count; no levels for a small scene. Throws <see cref="InvalidDataException"/> for a layout the
    /// pruner does not read.
    /// </summary>
    public static async Task<(int Count, List<SplatLodLevel> Levels)> SaveLadderAsync(
        byte[] spz, ICaptureFileStore files, CancellationToken ct)
    {
        var levels = new List<SplatLodLevel>();
        foreach (var (splats, bytes) in SplatLodLadder.Build(spz))
        {
            levels.Add(new SplatLodLevel(splats, await files.SaveAsync(bytes, ".spz", ct), bytes.LongLength));
        }

        return (SpzDecimator.CountOf(spz), levels);
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var built = await RunAsync(stoppingToken);
            if (built > 0)
            {
                logger.LogInformation("Built the level-of-detail ladder of {Count} photo-real scene(s)", built);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never block startup: the view keeps the legacy levels and the next start tries again.
            logger.LogWarning(ex, "Could not build the photo-real level-of-detail ladders");
        }
    }

    private async Task<bool> BuildOneAsync(Guid id, CancellationToken ct)
    {
        await using var db = dbContextFactory.CreateDbContext();
        var row = await db.WallGeometrySplats.FirstOrDefaultAsync(s => s.Id == id, ct);
        var spz = row is null ? null : await files.ReadAsync(row.StoredPath, ct);
        if (row is null || spz is null)
        {
            return false;
        }

        List<SplatLodLevel> levels;
        try
        {
            (row.SplatCount, levels) = await SaveLadderAsync(spz, files, ct);
        }
        catch (InvalidDataException ex)
        {
            logger.LogWarning("No level-of-detail ladder for photo-real scene {SplatId}: {Reason}", id, ex.Message);
            row.LodLevelsJson = SplatLodLadder.Serialize([]);
            await db.SaveChangesAsync(ct);
            return false;
        }

        row.LodLevelsJson = SplatLodLadder.Serialize(levels);
        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "Photo-real scene {SplatId}: {Splats} splats, ladder {Levels}",
            id, row.SplatCount, string.Join(" / ", levels.Select(l => $"{l.Splats} ({l.SizeBytes} B)")));
        return true;
    }
}
