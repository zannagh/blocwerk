using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Detection;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

/// <summary>
/// Drops crash-mat / floor FALSE detections from the OLD-generation carry set before they can be
/// matched or warp-carried onto the new capture. The detection-time filter only ever sees NEW YOLO
/// detections, so a mat that was already accepted at an earlier generation would otherwise be carried
/// forward for ever. Running the same pure, population-relative classifier over the old holds at the
/// point the carry set is built closes that hole: a dropped mat never enters matching, never gets a
/// successor, and never gets a <see cref="HoldGenerationLink"/>.
/// </summary>
public partial class WallBigUpdateService
{
    /// <summary>
    /// Returns the carried old holds with the mat/floor-signature rows removed. Reuses
    /// <see cref="MatFalseDetectionFilter"/> (the exact classifier the detector uses) over the old
    /// holds projected to <see cref="DetectedHold"/>s. SAFETY GUARD: a mat-signature hold that ANY
    /// boulder still references is never dropped — the false-positive rule must never silently strip a
    /// hold a committed boulder depends on. The dropped count is logged.
    /// </summary>
    private async Task<List<Hold>> FilterCarriedMatFalseHoldsAsync(BlocwerkDbContext db, IReadOnlyList<Hold> oldHolds)
    {
        // Below the classifier's minimum the population statistics are untrustworthy and Classify keeps
        // everything anyway; skip the projection/query work entirely.
        if (oldHolds.Count < MatFalseDetectionFilter.MinimumPopulationSize)
        {
            return oldHolds.ToList();
        }

        // Project each old Hold to a DetectedHold, keeping a reference-keyed back-map so the classifier's
        // Dropped set resolves to the exact Hold rows even when two holds share identical geometry.
        var detectionToHold = new Dictionary<DetectedHold, Hold>(ReferenceEqualityComparer.Instance);
        var detections = new List<DetectedHold>(oldHolds.Count);
        foreach (var hold in oldHolds)
        {
            var detection = new DetectedHold(hold.X, hold.Y, hold.Radius, hold.Color, hold.Confidence);
            detectionToHold[detection] = hold;
            detections.Add(detection);
        }

        var classified = MatFalseDetectionFilter.Classify(detections);
        if (classified.Dropped.Count == 0)
        {
            return oldHolds.ToList();
        }

        var droppedHoldIds = classified.Dropped
            .Select(d => detectionToHold[d].Id)
            .ToHashSet();

        // SAFETY GUARD: never drop an old hold a boulder still points at, mat signature or not.
        var boulderReferencedIds = (await db.BoulderHolds
                .Where(bh => droppedHoldIds.Contains(bh.HoldId))
                .Select(bh => bh.HoldId)
                .Distinct()
                .ToListAsync())
            .ToHashSet();

        var kept = oldHolds
            .Where(h => !droppedHoldIds.Contains(h.Id) || boulderReferencedIds.Contains(h.Id))
            .ToList();

        var droppedCount = oldHolds.Count - kept.Count;
        if (droppedCount > 0)
        {
            logger.LogInformation(
                "Big update carry: dropped {Count} crash-mat/floor false hold(s) from the carried old-generation set; {Guarded} mat-signature hold(s) kept because a boulder references them.",
                droppedCount,
                boulderReferencedIds.Count);
        }

        return kept;
    }
}
