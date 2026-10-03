using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry.TextureRegistration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

/// <summary>Reverting a run: exact, and only where nobody has moved or re-placed the hold since.</summary>
public sealed partial class HoldTexturePlacementService
{
    /// <inheritdoc/>
    public async Task<HoldPlacementRevertResult> RevertAsync(Guid wallId, Guid runId, CancellationToken ct = default)
    {
        var (db, userId) = await OpenForAdminAsync(wallId, ct);
        await using (db)
        {
            var run = await db.HoldPlacementRuns.FirstOrDefaultAsync(r => r.Id == runId && r.WallId == wallId, ct)
                      ?? throw new UserFacingException("That hold placement was not found on this wall.");
            if (run.RevertedAt is not null)
            {
                throw new UserFacingException("That hold placement has already been reverted.");
            }

            var entries = HoldPlacementEntry.FromJson(run.HoldsJson);
            var holds = new Dictionary<Guid, Hold>();
            foreach (var chunk in entries.Select(e => e.HoldId).Chunk(500))
            {
                var ids = chunk.ToList();
                foreach (var hold in await db.Holds.Where(h => ids.Contains(h.Id)).ToListAsync(ct))
                {
                    holds[hold.Id] = hold;
                }
            }

            var reverted = 0;
            var missing = 0;
            var edited = new List<Guid>();
            foreach (var entry in entries)
            {
                if (!holds.TryGetValue(entry.HoldId, out var hold))
                {
                    missing++;
                }
                else if (entry.TryRevert(hold))
                {
                    reverted++;
                }
                else
                {
                    edited.Add(entry.HoldId);
                }
            }

            run.RevertedAt = DateTimeOffset.UtcNow;
            run.RevertedCount = reverted;
            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "Hold placement {RunId} on wall {WallId} reverted by {UserId}: {Reverted} restored, {Edited} changed since, {Missing} deleted since",
                runId, wallId, userId, reverted, edited.Count, missing);
            return new HoldPlacementRevertResult(reverted, edited, missing);
        }
    }
}
