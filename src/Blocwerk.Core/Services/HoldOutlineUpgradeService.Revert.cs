using Blocwerk.Core.Detection.Outlines;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

/// <summary>Reverting a run: exact, and only where nobody has touched the hold since.</summary>
public sealed partial class HoldOutlineUpgradeService
{
    /// <inheritdoc/>
    public async Task<HoldOutlineRevertResult> RevertAsync(Guid wallId, Guid runId, CancellationToken ct = default)
    {
        var (db, userId) = await OpenForAdminAsync(wallId, ct);
        await using (db)
        {
            var run = await db.HoldOutlineUpgradeRuns.FirstOrDefaultAsync(r => r.Id == runId && r.WallId == wallId, ct)
                      ?? throw new InvalidOperationException("That outline upgrade was not found on this wall.");
            if (run.RevertedAt is not null)
            {
                throw new InvalidOperationException("That outline upgrade has already been reverted.");
            }

            var entries = HoldOutlineUpgradeEntry.FromJson(run.HoldIdsJson);
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
                else if (TryRevert(hold, entry))
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
                "Outline upgrade {RunId} on wall {WallId} reverted by {UserId}: {Reverted} restored, {Edited} edited since, {Missing} deleted since",
                runId, wallId, userId, reverted, edited.Count, missing);
            return new HoldOutlineRevertResult(reverted, edited, missing);
        }
    }

    /// <summary>
    /// Restores one hold when what the run wrote is still there. The outline is the deciding part: an
    /// outlined hold whose shape was edited since is left entirely alone. A fingerprint changed since (for
    /// example by a later wall update) is kept, while an untouched outline is still reverted.
    /// </summary>
    private static bool TryRevert(Hold hold, HoldOutlineUpgradeEntry entry)
    {
        var shapeIntact = entry.ShapeHash is not null && HoldOutlineUpgradeEntry.HashShape(hold) == entry.ShapeHash;
        var fingerprintIntact = entry.FingerprintHash is not null
                                && HoldOutlineUpgradeEntry.HashFingerprint(hold.FingerprintJson) == entry.FingerprintHash;
        if (entry.ShapeHash is not null ? !shapeIntact : !fingerprintIntact)
        {
            return false;
        }

        if (shapeIntact)
        {
            hold.ShapePoints = entry.PrevShapeEmpty ? [] : null;
            hold.ShapeHoles = null;
            hold.OutlineSource = entry.PrevOutlineSource;
            entry.PrevMetric?.RestoreTo(hold);
        }

        if (fingerprintIntact)
        {
            hold.FingerprintJson = entry.PrevFingerprintJson;
        }

        return true;
    }
}
