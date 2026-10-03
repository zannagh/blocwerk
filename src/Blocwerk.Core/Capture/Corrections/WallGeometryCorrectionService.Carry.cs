// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry.TextureRegistration;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Capture.Corrections;

/// <summary>Bringing a corrected version made before corrections carried their data up to its parent's placements.</summary>
public sealed partial class WallGeometryCorrectionService
{
    public async Task<CorrectionCarryResult> CarryFromParentAsync(Guid wallId)
    {
        var (db, userId) = await OpenForAdminAsync(wallId);
        await using (db)
        {
            var model = await db.WallGeometryModels.AsNoTracking().FirstOrDefaultAsync(m => m.WallId == wallId && m.IsActive)
                        ?? throw new UserFacingException("This wall has no 3D model yet.");
            if (model.DerivedFromModelId is not { } parentId || !model.Source.StartsWith(SourcePrefix, StringComparison.Ordinal))
            {
                throw new UserFacingException("The active 3D model is not a corrected version; there is nothing to carry over.");
            }

            await using var transaction = await db.Database.BeginTransactionAsync();
            var reverted = await RevertRunsAsync(db, wallId, model.Id);
            var result = await CorrectionCarry.CarryAsync(db, wallId, parentId, model.Id, userId)
                         ?? throw new UserFacingException("The correction's change of the model can no longer be read, so nothing was carried over.");
            (int Placed, int Changed) volumes = await db.WallVolumes.AnyAsync(v => v.GeometryModelId == model.Id)
                ? await WallVolumeService.PlaceHoldsAsync(db, wallId, model.Id, CancellationToken.None)
                : (0, 0);
            await transaction.CommitAsync();
            logger.LogInformation(
                "Wall {WallId}: model {ModelId} brought to its parent {ParentId}'s data by {UserId}: {Reverted} holds of its own runs reverted, "
                + "{Result}, {OnVolumes} holds placed on its volumes again",
                wallId, model.Id, parentId, userId, reverted, result, volumes.Placed);
            return result;
        }
    }

    /// <summary>
    /// Reverts every unreverted placement run on the model, newest first, where nobody changed the hold since: what the
    /// follow-ups registered (or carried) on it goes, the data it was derived from comes back. Returns the holds restored.
    /// </summary>
    private static async Task<int> RevertRunsAsync(BlocwerkDbContext db, Guid wallId, Guid modelId)
    {
        // Few rows per wall; ordered in memory because SQLite cannot ORDER BY a DateTimeOffset.
        var runs = await db.HoldPlacementRuns.Where(r => r.WallId == wallId && r.GeometryModelId == modelId && r.RevertedAt == null).ToListAsync();
        var total = 0;
        foreach (var run in runs.OrderByDescending(r => r.CreatedAt))
        {
            var entries = HoldPlacementEntry.FromJson(run.HoldsJson);
            var ids = entries.Select(e => e.HoldId).ToList();
            var holds = new Dictionary<Guid, Hold>();
            foreach (var chunk in ids.Chunk(500))
            {
                foreach (var hold in await db.Holds.Where(h => chunk.Contains(h.Id)).ToListAsync())
                {
                    holds[hold.Id] = hold;
                }
            }

            var reverted = entries.Count(e => holds.TryGetValue(e.HoldId, out var hold) && e.TryRevert(hold));
            (run.RevertedAt, run.RevertedCount) = (DateTimeOffset.UtcNow, reverted);
            total += reverted;
            await db.SaveChangesAsync();
        }

        return total;
    }
}
