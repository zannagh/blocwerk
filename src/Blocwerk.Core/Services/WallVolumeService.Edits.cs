// <copyright file="WallVolumeService.Edits.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

/// <summary>A wall admin's corrections of the detected volumes: remove / restore and flat sides (per volume and for the wall).</summary>
public sealed partial class WallVolumeService
{
    /// <inheritdoc />
    public async Task<WallVolumeRunResult> SetRemovedAsync(Guid wallId, Guid volumeId, bool removed, CancellationToken ct = default)
    {
        await EnsureAdminAsync(wallId, ct);
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var volume = await FindAsync(db, wallId, volumeId, ct);
        (volume.IsRemoved, volume.RemovedAt) = removed ? (true, DateTimeOffset.UtcNow) : (false, (DateTimeOffset?)null);
        await db.SaveChangesAsync(ct);
        var (placed, changed) = await ReplaceHoldsAsync(db, wallId, volume.GeometryModelId, ct);
        var total = await db.WallVolumes.CountAsync(v => v.GeometryModelId == volume.GeometryModelId && !v.IsRemoved, ct);
        logger.LogInformation("Volume {VolumeId} on wall {WallId} {Action}; {Changed} holds re-placed", volumeId, wallId, removed ? "removed" : "restored", changed);
        return new WallVolumeRunResult(total, 0, placed, changed);
    }

    /// <inheritdoc />
    public async Task<WallVolumeShapeResult> SetFlatSidesAsync(Guid wallId, Guid volumeId, bool value, CancellationToken ct = default)
    {
        await EnsureAdminAsync(wallId, ct);
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var volume = await FindAsync(db, wallId, volumeId, ct);
        var fit = WallVolumeShapes.SetFlatSides(volume, value, force: true);
        if (value && !volume.HasFlatSides)
        {
            throw new UserFacingException("This volume's measurement is too small or flat to give it flat sides.");
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "Volume {VolumeId} on wall {WallId}: flat sides {Value} ({Shape}, RMS {Rms} mm)", volumeId, wallId, value, fit?.Polyhedron.Shape, fit?.RmsMm);
        return await ShapeResultAsync(db, wallId, volume.GeometryModelId, 0, ct);
    }

    /// <inheritdoc />
    public async Task<bool> GetWallFlatSidesAsync(Guid wallId, CancellationToken ct = default)
    {
        await EnsureAdminAsync(wallId, ct);
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        return await db.Walls.AsNoTracking().Where(w => w.Id == wallId).Select(w => w.VolumesHaveFlatSides).FirstOrDefaultAsync(ct);
    }

    /// <inheritdoc />
    public async Task<WallVolumeShapeResult> SetWallFlatSidesAsync(Guid wallId, bool value, bool applyToAll, CancellationToken ct = default)
    {
        await EnsureAdminAsync(wallId, ct);
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var wall = await db.Walls.FirstOrDefaultAsync(w => w.Id == wallId, ct) ?? throw new UserFacingException("That wall does not exist.");
        wall.VolumesHaveFlatSides = value;
        var modelId = await ActiveModelIdAsync(db, wallId, ct);
        var kept = 0;
        if (applyToAll && modelId is { } model)
        {
            foreach (var volume in await db.WallVolumes.Where(v => v.GeometryModelId == model && !v.IsRemoved).ToListAsync(ct))
            {
                // The admin asked for all of them: forced like a ticked volume; only newly detected ones keep the quality limit.
                WallVolumeShapes.SetFlatSides(volume, value, force: true);
                if (value && !volume.HasFlatSides)
                {
                    kept++;
                    logger.LogInformation(
                        "Volume {Index} on wall {WallId} stays on its height field: too small or flat for flat sides", volume.Index, wallId);
                }
            }
        }

        await db.SaveChangesAsync(ct);
        return modelId is { } id ? await ShapeResultAsync(db, wallId, id, kept, ct) : new WallVolumeShapeResult(0, 0, 0, 0);
    }

    private static async Task<WallVolume> FindAsync(BlocwerkDbContext db, Guid wallId, Guid volumeId, CancellationToken ct) =>
        await db.WallVolumes.FirstOrDefaultAsync(v => v.Id == volumeId && v.WallId == wallId, ct)
            ?? throw new UserFacingException("That volume does not exist on this wall.");

    private async Task<WallVolumeShapeResult> ShapeResultAsync(BlocwerkDbContext db, Guid wallId, Guid modelId, int kept, CancellationToken ct)
    {
        var (placed, changed) = await ReplaceHoldsAsync(db, wallId, modelId, ct);
        var flat = await db.WallVolumes.CountAsync(v => v.GeometryModelId == modelId && !v.IsRemoved && v.HasFlatSides, ct);
        return new WallVolumeShapeResult(flat, kept, placed, changed);
    }

    /// <summary>
    /// Re-places the holds after an admin changed the volumes, and queues the holds that moved for their footprint and
    /// protrusion to follow (<see cref="IHoldRefinementQueue"/>).
    /// </summary>
    private async Task<(int Placed, int Changed)> ReplaceHoldsAsync(BlocwerkDbContext db, Guid wallId, Guid modelId, CancellationToken ct)
    {
        var moved = new List<Guid>();
        var result = await PlaceHoldsAsync(db, wallId, modelId, moved, ct);
        refinementQueue?.Enqueue(wallId, moved);
        return result;
    }
}
